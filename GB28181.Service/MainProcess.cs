﻿using Logger4Net;
using SIPSorcery.GB28181.Servers.SIPMessage;
using SIPSorcery.GB28181.Sys;
using SIPSorcery.GB28181.Sys.Config;
using SIPSorcery.GB28181.Sys.XML;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using GrpcAgent;
using GrpcAgent.WebsocketRpcServer;
using MediaContract;
using SIPSorcery.GB28181.Servers;
using System.IO;
using Microsoft.Extensions.Configuration;
using SIPSorcery.GB28181.SIP;
using SIPSorcery.GB28181.Servers.SIPMonitor;
using SIPSorcery.GB28181.Sys.Cache;
using SIPSorcery.GB28181.Sys.Model;
using GrpcPtzControl;
using GrpcDeviceCatalog;
using Grpc.Core;
//using GrpcGb28181Config;
using GrpcDeviceFeature;
using Consul;
using System.Net;
using GrpcVideoOnDemand;
using Manage;

namespace GB28181Service
{
    public class MainProcess : IDisposable
    {
        private static ILog logger = AppState.GetLogger("Startup");
        //interface IDisposable implementation
        private bool _already_disposed = false;

        // Thread signal for stop work.
        private readonly ManualResetEvent _eventStopService = new ManualResetEvent(false);

        // Thread signal for infor thread is over.
        private readonly ManualResetEvent _eventThreadExit = new ManualResetEvent(false);

        //signal to exit the main Process
        private readonly AutoResetEvent _eventExitMainProcess = new AutoResetEvent(false);

        private Task _mainTask = null;
        private Task _sipTask = null;
        private Task _registerTask = null;

        private Task _mainWebSocketRpcTask = null;

        private DateTime _keepaliveTime;
        private Queue<HeartBeatEndPoint> _keepAliveQueue = new Queue<HeartBeatEndPoint>();

        private Queue<SIPSorcery.GB28181.Sys.XML.Catalog> _catalogQueue = new Queue<SIPSorcery.GB28181.Sys.XML.Catalog>();

        private readonly ServiceCollection servicesContainer = new ServiceCollection();

        private ServiceProvider _serviceProvider = null;

        private AgentServiceRegistration _AgentServiceRegistration = null;

        public MainProcess()
        {

        }

        #region IDisposable interface

        public void Dispose()
        {
            // tell the GC that the Finalize process no longer needs to be run for this object.
            GC.SuppressFinalize(this);
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            lock (this)
            {
                if (_already_disposed)
                    return;

                if (disposing)
                {
                    _eventStopService.Close();
                    _eventThreadExit.Close();
                }
            }

            _already_disposed = true;
        }

        #endregion

        public void Run()
        {
            _eventStopService.Reset();
            _eventThreadExit.Reset();

            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);

            // config info for .net core https://www.cnblogs.com/Leo_wl/p/5745772.html#_label3
            var builder = new ConfigurationBuilder();
                //.SetBasePath(Directory.GetCurrentDirectory())
                //.AddXmlFile("Config/gb28181.xml", false, reloadOnChange: true);
            var config = builder.Build();//Console.WriteLine(config["sipaccount:ID"]);
            //var sect = config.GetSection("sipaccounts");

            //Consul Register
            //ServiceRegister();
            //InitServer
            SipAccountStorage.RPCGBServerConfigReceived += SipAccountStorage_RPCGBServerConfigReceived;

            //Config Service & and run
            ConfigServices(config);

            //Start the main serice
            _mainTask = Task.Factory.StartNew(() => MainServiceProcessing());

            //wait the process exit of main
            _eventExitMainProcess.WaitOne();
        }
        
        public void Stop()
        {
            // signal main service exit
            _eventStopService.Set();
            _eventThreadExit.WaitOne();
        }

        private void ConfigServices(IConfigurationRoot configuration)
        {
            //we should initialize resource here then use them.
            servicesContainer.AddSingleton(configuration)  // add configuration 
                            .AddSingleton<ILog, Logger>()
                            .AddSingleton<ISipAccountStorage, SipAccountStorage>()
                            .AddSingleton<MediaEventSource>()
                            .AddSingleton<MessageCenter>()
                            .AddScoped<ISIPServiceDirector, SIPServiceDirector>()
                            .AddTransient<ISIPMonitorCore, SIPMonitorCoreService>()
                            .AddSingleton<ISipMessageCore, SIPMessageCoreService>()
                            .AddSingleton<ISIPTransport, SIPTransport>()
                            .AddTransient<ISIPTransactionEngine, SIPTransactionEngine>()
                            .AddSingleton<ISIPRegistrarCore, SIPRegistrarCoreService>()
                            .AddSingleton<IMemoCache<Camera>, DeviceObjectCache>()
                            .AddSingleton<IRpcService, RpcServer>()
                            .AddScoped<VideoSession.VideoSessionBase, SSMediaSessionImpl>()
                            .AddScoped<PtzControl.PtzControlBase, PtzControlImpl>()
                            .AddScoped<DeviceCatalog.DeviceCatalogBase, DeviceCatalogImpl>()
                            .AddScoped<Manage.Manage.ManageBase, DeviceManageImpl>()
                            .AddScoped<DeviceFeature.DeviceFeatureBase, DeviceFeatureImpl>()
                            .AddScoped<VideoOnDemand.VideoOnDemandBase, VideoOnDemandImpl>()
                            .AddSingleton<IServiceCollection>(servicesContainer); // add itself 
            _serviceProvider = servicesContainer.BuildServiceProvider();
        }
        
        private void MainServiceProcessing()
        {
            _keepaliveTime = DateTime.Now;
            try
            {
                var _mainSipService = _serviceProvider.GetRequiredService<ISipMessageCore>();
                //Get meassage Handler
                var messageHandler = _serviceProvider.GetRequiredService<MessageCenter>();                
                // start the Listening SipService in main Service
                _sipTask = Task.Factory.StartNew(() =>
                {
                    _mainSipService.OnKeepaliveReceived += messageHandler.OnKeepaliveReceived;
                    _mainSipService.OnServiceChanged += messageHandler.OnServiceChanged;
                    //_mainSipService.OnCatalogReceived += messageHandler.OnCatalogReceived;
                    //_mainSipService.OnNotifyCatalogReceived += messageHandler.OnNotifyCatalogReceived;
                    _mainSipService.OnAlarmReceived += messageHandler.OnAlarmReceived;
                    //_mainSipService.OnRecordInfoReceived += messageHandler.OnRecordInfoReceived;
                    //_mainSipService.OnDeviceStatusReceived += messageHandler.OnDeviceStatusReceived;
                    _mainSipService.OnDeviceInfoReceived += messageHandler.OnDeviceInfoReceived;
                    _mainSipService.OnMediaStatusReceived += messageHandler.OnMediaStatusReceived;
                    _mainSipService.OnPresetQueryReceived += messageHandler.OnPresetQueryReceived;
                    _mainSipService.OnDeviceConfigDownloadReceived += messageHandler.OnDeviceConfigDownloadReceived;
                    _mainSipService.Start();

                });

                // run the register service
                var _registrarCore = _serviceProvider.GetRequiredService<ISIPRegistrarCore>();
                _registerTask = Task.Factory.StartNew(() =>
                {
                    _registrarCore.ProcessRegisterRequest();
                });

                //Run the Rpc Server End
                var _mainWebSocketRpcServer = _serviceProvider.GetRequiredService<IRpcService>();
                _mainWebSocketRpcTask = Task.Factory.StartNew(() =>
                {
                    _mainWebSocketRpcServer.AddIPAdress("0.0.0.0");
                    _mainWebSocketRpcServer.AddPort(EnvironmentVariables.GBServerGrpcPort);//50051
                    _mainWebSocketRpcServer.Run();
                });

                //video session alive
                var videosessionalive = VideoSessionKeepAlive();

                ////test code will be removed
                //var abc = WaitUserCmd();
                //abc.Wait();

                //wait main service exit
                _eventStopService.WaitOne();

                //signal main process exit
                _eventExitMainProcess.Set();
            }
            catch (Exception exMsg)
            {
                logger.Error(exMsg.Message);
                _eventExitMainProcess.Set();
            }
            finally
            {

            }
        }

        #region Init Server
        private List<SIPSorcery.GB28181.SIP.App.SIPAccount> SipAccountStorage_RPCGBServerConfigReceived()
        {
            try
            {
                string GBServerChannelAddress = EnvironmentVariables.GBServerChannelAddress ?? "devicemanagementservice:8080";//devicemanagementservice
                Channel channel = new Channel(GBServerChannelAddress, ChannelCredentials.Insecure);
                var client = new ManageGbService.ManageGbServiceClient(channel);
                //GbConfigRequest _GbConfigRequest = new GbConfigRequest();
                QueryGb28181ConfigReply _GbConfigReply = new QueryGb28181ConfigReply();
                _GbConfigReply = client.GetGb28181ServiceConfig(new QueryGb28181ConfigRequest() { });

                List<SIPSorcery.GB28181.SIP.App.SIPAccount> _lstSIPAccount = new List<SIPSorcery.GB28181.SIP.App.SIPAccount>();
                foreach (SIPAccount item in _GbConfigReply.Sipaccount)
                {
                    SIPSorcery.GB28181.SIP.App.SIPAccount obj = new SIPSorcery.GB28181.SIP.App.SIPAccount();
                    obj.Id = Guid.NewGuid();
                    //obj.Owner = item.Name;
                    obj.GbVersion = string.IsNullOrEmpty(item.GbVersion) ? "GB-2016" : item.GbVersion;
                    obj.LocalID = string.IsNullOrEmpty(item.LocalID) ? "42010000002100000002" : item.LocalID;
                    obj.LocalIP = System.Net.IPAddress.Parse(GetIPAddress()); //System.Net.IPAddress.Parse(item.LocalIP);
                    obj.LocalPort = string.IsNullOrEmpty(item.LocalPort) ? Convert.ToUInt16(5061) : Convert.ToUInt16(item.LocalPort);
                    obj.RemotePort = string.IsNullOrEmpty(item.RemotePort) ? Convert.ToUInt16(5060) : Convert.ToUInt16(item.RemotePort);
                    obj.Authentication = string.IsNullOrEmpty(item.Authentication) ? false : Boolean.Parse(item.Authentication);
                    obj.SIPUsername = string.IsNullOrEmpty(item.SIPUsername) ? "admin" : item.SIPUsername;
                    obj.SIPPassword = string.IsNullOrEmpty(item.SIPPassword) ? "123456" : item.SIPPassword;
                    obj.MsgProtocol = System.Net.Sockets.ProtocolType.Udp;
                    obj.StreamProtocol = System.Net.Sockets.ProtocolType.Udp;
                    obj.TcpMode = SIPSorcery.GB28181.Net.RTP.TcpConnectMode.passive;
                    obj.MsgEncode = string.IsNullOrEmpty(item.MsgEncode) ? "GB2312" : item.MsgEncode;
                    obj.PacketOutOrder = string.IsNullOrEmpty(item.PacketOutOrder) ? true : Boolean.Parse(item.PacketOutOrder);
                    obj.KeepaliveInterval = string.IsNullOrEmpty(item.KeepaliveInterval) ? Convert.ToUInt16(5000) : Convert.ToUInt16(item.KeepaliveInterval);
                    obj.KeepaliveNumber = string.IsNullOrEmpty(item.KeepaliveNumber) ? Convert.ToByte(3) : Convert.ToByte(item.KeepaliveNumber);
                    _lstSIPAccount.Add(obj);
                }
                return _lstSIPAccount;
            }
            catch (Exception ex)
            {
                logger.Debug("Can't get gb info from device-mgr, it will get gb info from config.");
                return null;
            }
        }

        private async Task VideoSessionKeepAlive()
        {
            await Task.Run(() =>
             {
                 var mockCaller = _serviceProvider.GetService<ISIPServiceDirector>();
                 while (true)
                 {
                     for (int i = 0; i < mockCaller.VideoSessionAlive.ToArray().Length; i++)
                     {
                         Dictionary<string, DateTime> dict = mockCaller.VideoSessionAlive[i];
                         foreach (string key in dict.Keys)
                         {
                             TimeSpan ts1 = new TimeSpan(DateTime.Now.Ticks);
                             TimeSpan ts2 = new TimeSpan(Convert.ToDateTime(dict[key]).Ticks);
                             TimeSpan ts = ts1.Subtract(ts2).Duration();
                             if (ts.Seconds > 30)
                             {
                                 mockCaller.Stop(key.ToString().Split(',')[0], key.ToString().Split(',')[1]);
                                 mockCaller.VideoSessionAlive.RemoveAt(i);
                             }
                         }
                     }
                 }
             });
        }

        //private async Task WaitUserCmd()
        //{
        //    await Task.Run(() =>
        //     {
        //         while (true)
        //         {
        //             Console.WriteLine("\ninput command : I -Invite, E -Exit");
        //             var inputkey = Console.ReadKey();
        //             switch (inputkey.Key)
        //             {
        //                 case ConsoleKey.I:
        //                     {
        //                         var mockCaller = _serviceProvider.GetService<ISIPServiceDirector>();
        //                         mockCaller.MakeVideoRequest("42010000001310000184", new int[] { 5060 }, EnvironmentVariables.LocalIp);
        //                     }
        //                     break;
        //                 case ConsoleKey.E:
        //                     Console.WriteLine("\nexit Process!");
        //                     break;
        //                 default:
        //                     break;
        //             }
        //             if (inputkey.Key == ConsoleKey.E)
        //             {
        //                 return 0;
        //             }
        //             else
        //             {
        //                 continue;
        //             }
        //         }
        //     });
        //}

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject != null)
            {
                if (e.ExceptionObject is Exception exceptionObj)
                {
                    throw exceptionObj;
                }
            }
        }
        #endregion

        #region Consul Register
        public string GetIPAddress()
        {
            string hostname = Dns.GetHostName();
            IPHostEntry ipadrlist = Dns.GetHostByName(hostname);
            IPAddress localaddr = null;
            foreach (IPAddress obj in ipadrlist.AddressList)
            {
                localaddr = obj;
            }
            logger.Debug("Local machine Dns: " + localaddr.ToString());
            return localaddr.ToString();
        }
        /// <summary>
        /// Consul Register
        /// </summary>
        /// <param name="client"></param>
        private void ServiceRegister()
        {
            try
            {
                var clients = new ConsulClient(ConfigurationOverview);
                _AgentServiceRegistration = new AgentServiceRegistration()
                {
                    Address = "10.78.115.182",//GetIPAddress(),
                    ID = "gb28181" + Dns.GetHostName(),
                    Name = "gb28181",
                    Port = EnvironmentVariables.GBServerGrpcPort,
                    Tags = new[] { "gb28181" }
                };
                var result = clients.Agent.ServiceRegister(_AgentServiceRegistration).Result;
                logger.Debug("GB server("+ "gb28181" + Dns.GetHostName() + ") registering consul completed.");
            }
            catch (Exception ex)
            {
                logger.Error("Consul Register: " + ex.Message);
            }
        }
        private void ConfigurationOverview(ConsulClientConfiguration obj)
        {
            obj.Address = new Uri("http://" + (EnvironmentVariables.MicroRegistryAddress ?? "10.78.115.124:8500"));
            obj.Datacenter = "dc1";
        }
        #endregion
    }
}
