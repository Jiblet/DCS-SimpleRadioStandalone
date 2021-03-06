﻿using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Ciribob.DCS.SimpleRadio.Standalone.Common;
using Ciribob.DCS.SimpleRadio.Standalone.Server;
using Newtonsoft.Json;
using NLog;

namespace Ciribob.DCS.SimpleRadio.Standalone.Client
{
    internal class ClientSync
    {
        public delegate void ConnectCallback(bool result);

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private ConnectCallback _callback;
        private readonly ConcurrentDictionary<string, SRClient> _clients;
        private readonly string _guid;
        private IPEndPoint _serverEndpoint;
        private TcpClient _tcpClient;

        public ClientSync(ConcurrentDictionary<string, SRClient> clients, string guid)
        {
            this._clients = clients;
            this._guid = guid;
        }


        public void TryConnect(IPEndPoint endpoint, ConnectCallback callback)
        {
            this._callback = callback;
            _serverEndpoint = endpoint;

            var tcpThread = new Thread(Connect);
            tcpThread.Start();
        }

        private void Connect()
        {
            var radioSync = new RadioSyncServer(ClientRadioUpdated, ClientSideUpdate);
            using (_tcpClient = new TcpClient())
            {
                _tcpClient.SendTimeout = 10;
                try
                {
                    _tcpClient.Connect(_serverEndpoint);

                    if (_tcpClient.Connected)
                    {
                        radioSync.Listen();

                        _tcpClient.NoDelay = true;

                        CallOnMain(true);
                        ClientSyncLoop();
                    }
                }
                catch (SocketException ex)
                {
                    Logger.Error(ex, "error connecting to server");
                }
            }

            radioSync.Stop();

            //disconnect callback
            CallOnMain(false);
        }

        private void ClientRadioUpdated()
        {
          //DO NOTHING FOR NOW
        }

        private void ClientSideUpdate()
        {
            SendToServer(new NetworkMessage
            {
                Client = new SRClient
                {
                    Coalition = RadioSyncServer.DcsPlayerSideInfo.side,
                    Name = RadioSyncServer.DcsPlayerSideInfo.name,
                    ClientGuid = _guid
                },
                MsgType = NetworkMessage.MessageType.UPDATE
            });
        }

        private void CallOnMain(bool result)
        {
            try
            {
                Application.Current.Dispatcher.Invoke(DispatcherPriority.Background,
                    new ThreadStart(delegate { _callback(result); }));
            }
            catch (Exception ex)
            {
            }
        }

        private void ClientSyncLoop()
        {
            //clear the clietns list
            _clients.Clear();

            using (var reader = new StreamReader(_tcpClient.GetStream(), Encoding.UTF8))
            {
                try
                {
                    //start the loop off by sending a SYNC Request
                    SendToServer(new NetworkMessage
                    {
                        Client = new SRClient
                        {
                            Coalition = RadioSyncServer.DcsPlayerSideInfo.side,
                            Name = RadioSyncServer.DcsPlayerSideInfo.name,
                            ClientGuid = _guid
                        },
                        MsgType = NetworkMessage.MessageType.SYNC
                    });


                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        try
                        {
                            var lastRadioTransmit = JsonConvert.DeserializeObject<NetworkMessage>(line);
                            //TODO: test malformed JSON
                            if (lastRadioTransmit != null)
                            {
                                switch (lastRadioTransmit.MsgType)
                                {
                                    case NetworkMessage.MessageType.PING:
                                        // Do nothing for now
                                        break;
                                    case NetworkMessage.MessageType.UPDATE:

                                        Logger.Info("Recevied: " + NetworkMessage.MessageType.UPDATE + " From: " +
                                                    lastRadioTransmit.Client.Name + " Coalition: " +
                                                    lastRadioTransmit.Client.Coalition);

                                        if (_clients.ContainsKey(lastRadioTransmit.Client.ClientGuid))
                                        {
                                            var srClient = _clients[lastRadioTransmit.Client.ClientGuid];
                                            var updatedSrClient = lastRadioTransmit.Client;
                                            if (srClient != null)
                                            {
                                                srClient.LastUpdate = Environment.TickCount;
                                                srClient.Name = updatedSrClient.Name;
                                                srClient.Coalition = updatedSrClient.Coalition;
                                                srClient.LastUpdate = Environment.TickCount;
                                            }
                                        }
                                        else
                                        {
                                            var connectedClient = lastRadioTransmit.Client;
                                            connectedClient.LastUpdate = Environment.TickCount;
                                            _clients[lastRadioTransmit.Client.ClientGuid] = connectedClient;
                                        }
                                        break;
                                    case NetworkMessage.MessageType.SYNC:
                                        Logger.Info("Recevied: " + NetworkMessage.MessageType.SYNC);

                                        if (lastRadioTransmit.Clients != null)
                                        {
                                            foreach (var client in lastRadioTransmit.Clients)
                                            {
                                                client.LastUpdate = Environment.TickCount;
                                                _clients[client.ClientGuid] = client;
                                            }
                                        }

                                        break;
                                    default:
                                        Logger.Warn("Recevied unknown " + line);
                                        break;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, "Client exception reading from socket ");
                        }

                        // do something with line
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Client exception reading - Disconnecting ");
                }
            }
            //clear the clietns list
            _clients.Clear();

            //disconnect callback
            CallOnMain(false);
        }


        private void SendToServer(NetworkMessage message)
        {
            try
            {
                var json = Encoding.ASCII.GetBytes(JsonConvert.SerializeObject(message) + "\n");

                _tcpClient.GetStream().Write(json, 0, json.Length);
                //Need to flush?
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Client exception sending so server");

                Disconnect();
            }
        }

        //implement IDispose? To close stuff properly?
        public void Disconnect()
        {
            try
            {
                if (_tcpClient != null)
                {
                    _tcpClient.Close(); // this'll stop the socket blocking
                }
            }
            catch (Exception ex)
            {
            }

            Logger.Warn("Disconnecting from server");

            //CallOnMain(false);
        }
    }
}