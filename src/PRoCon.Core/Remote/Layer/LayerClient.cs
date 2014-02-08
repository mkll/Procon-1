﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using PRoCon.Core.Remote.Layer.PacketDispatchers;

namespace PRoCon.Core.Remote.Layer {
    using Core;
    using Core.Plugin;
    using Core.Accounts;
    using Core.Battlemap;
    using Core.Remote;

    public class LayerClient {

        public static string ResponseOk = "OK";
        
        public static string ResponseInvalidPasswordHash = "InvalidPasswordHash";
        public static string ResponseInvalidPassword = "InvalidPassword";
        public static string ResponseInvalidUsername = "InvalidUsername";
        public static string ResponseLoginRequired = "LogInRequired";
        public static string ResponseInsufficientPrivileges = "InsufficientPrivileges";
        public static string ResponseInvalidArguments = "InvalidArguments";
        public static string ResponseUnknownCommand = "UnknownCommand";

        public delegate void LayerClientHandler(LayerClient sender);
        public event LayerClientHandler ClientShutdown;
        public event LayerClientHandler Login;
        public event LayerClientHandler Logout;
        public event LayerClientHandler Quit;
        public event LayerClientHandler UidRegistered;

        private PRoConApplication m_praApplication;
        private PRoConClient m_prcClient;

        protected delegate void RequestPacketHandler(ILayerPacketDispatcher sender, Packet packet);
        protected Dictionary<string, RequestPacketHandler> m_requestDelegates;

        /// <summary>
        /// The game dependant packet dispatcher to use
        /// </summary>
        public ILayerPacketDispatcher PacketDispatcher { get; set; }

        /// <summary>
        /// If the client has authenticated yet
        /// </summary>
        protected bool IsLoggedIn { get; set; }

        /// <summary>
        /// If the client has events enabled (wants to recieve events)
        /// </summary>
        protected bool EventsEnabled { get; set; }

        /// <summary>
        /// Uid the client has chosen to identify them selves as
        /// </summary>
        public String ProconEventsUid { get; protected set; }

        /// <summary>
        /// The username the client has authenticated with. See IsLoggedIn.
        /// </summary>
        public String Username { get; private set; }

        /// <summary>
        /// The privileges of the authenticated user
        /// </summary>
        public CPrivileges Privileges { get; private set; }

        /// <summary>
        /// If gzip compression should be used during transport
        /// </summary>
        public bool GzipCompression { get; private set; }

        /// <summary>
        /// The salt issued to the client for authentication
        /// </summary>
        protected String Salt { get; set; }

        public LayerClient(ILayerConnection newConnection, PRoConApplication praApplication, PRoConClient prcClient) {
            Privileges = new CPrivileges();
            Username = String.Empty;

            // This is just a default value so we never accidently pass through an empty
            // string for authentication. We generate a better salt later on.
            this.Salt = DateTime.Now.ToString("HH:mm:ss ff");

            this.IsLoggedIn = false;
            this.GzipCompression = false;

            this.ProconEventsUid = String.Empty;

            if (prcClient.Game != null) {

                if (prcClient.Game is BFBC2Client) {
                    this.PacketDispatcher = new Bfbc2PacketDispatcher(newConnection);
                }
                else if (prcClient.Game is MoHClient) {
                    this.PacketDispatcher = new MohPacketDispatcher(newConnection);
                }
                else if (prcClient.Game is BF3Client) {
                    this.PacketDispatcher = new Bf3PacketDispatcher(newConnection);
                }
                else if (prcClient.Game is BF4Client) {
                    this.PacketDispatcher = new Bf4PacketDispatcher(newConnection);
                }
                else if (prcClient.Game is MOHWClient)
                {
                    this.PacketDispatcher = new MohwPacketDispatcher(newConnection);
                }

                this.m_requestDelegates = new Dictionary<string, RequestPacketHandler>() {
                    { "procon.application.shutdown", this.DispatchProconApplicationShutdownRequest  },

                    { "procon.login.username", this.DispatchProconLoginUsernameRequest  },
                    { "procon.registerUid", this.DispatchProconRegisterUidRequest  },
                    { "procon.version", this.DispatchProconVersionRequest  },
                    { "procon.vars", this.DispatchProconVarsRequest  },
                    { "procon.privileges", this.DispatchProconPrivilegesRequest  },
                    { "procon.compression", this.DispatchProconCompressionRequest  },

                    { "procon.account.listAccounts", this.DispatchProconAccountListAccountsRequest  },
                    { "procon.account.listLoggedIn", this.DispatchProconAccountListLoggedInRequest  },
                    { "procon.account.create", this.DispatchProconAccountCreateRequest  },
                    { "procon.account.delete", this.DispatchProconAccountDeleteRequest  },
                    { "procon.account.setPassword", this.DispatchProconAccountSetPasswordRequest  },

                    { "procon.battlemap.deleteZone", this.DispatchProconBattlemapDeleteZoneRequest  },
                    { "procon.battlemap.createZone", this.DispatchProconBattlemapCreateZoneRequest  },
                    { "procon.battlemap.modifyZoneTags", this.DispatchProconBattlemapModifyZoneTagsRequest  },
                    { "procon.battlemap.modifyZonePoints", this.DispatchProconBattlemapModifyZonePointsRequest  },
                    { "procon.battlemap.listZones", this.DispatchProconBattlemapListZonesRequest  },

                    { "procon.layer.setPrivileges", this.DispatchProconLayerSetPrivilegesRequest  },

                    { "procon.plugin.listLoaded", this.DispatchProconPluginListLoadedRequest  },
                    { "procon.plugin.listEnabled", this.DispatchProconPluginListEnabledRequest  },
                    { "procon.plugin.enable", this.DispatchProconPluginEnableRequest  },
                    { "procon.plugin.setVariable", this.DispatchProconPluginSetVariableRequest  },

                    { "procon.exec", this.DispatchProconExecRequest },

                    { "procon.admin.say", this.DispatchProconAdminSayRequest },
                    { "procon.admin.yell", this.DispatchProconAdminYellRequest },
                };

                if ((this.m_praApplication = praApplication) != null && (this.m_prcClient = prcClient) != null) {
                    this.RegisterEvents();
                }
            }
        }

        private void RegisterEvents() {
            if (this.PacketDispatcher != null) {
                this.PacketDispatcher.ConnectionClosed =PacketDispatcher_ConnectionClosed;

                this.PacketDispatcher.RequestPacketUnknownRecieved = PacketDispatcher_RequestPacketUnknownRecieved;
                this.PacketDispatcher.RequestLoginHashed = PacketDispatcher_RequestLoginHashed;
                this.PacketDispatcher.RequestLoginHashedPassword = PacketDispatcher_RequestLoginHashedPassword;
                this.PacketDispatcher.RequestLoginPlainText = PacketDispatcher_RequestLoginPlainText;
                this.PacketDispatcher.RequestLogout = PacketDispatcher_RequestLogout;
                this.PacketDispatcher.RequestQuit = PacketDispatcher_RequestQuit;
                this.PacketDispatcher.RequestHelp = PacketDispatcher_RequestHelp;
                this.PacketDispatcher.RequestPacketAdminShutdown = PacketDispatcher_RequestPacketAdminShutdown;

                this.PacketDispatcher.RequestEventsEnabled = PacketDispatcher_RequestEventsEnabled;

                this.PacketDispatcher.RequestPacketSecureSafeListedRecieved = PacketDispatcher_RequestPacketSecureSafeListedRecieved;
                this.PacketDispatcher.RequestPacketUnsecureSafeListedRecieved = PacketDispatcher_RequestPacketUnsecureSafeListedRecieved;

                this.PacketDispatcher.RequestPacketPunkbusterRecieved = PacketDispatcher_RequestPacketPunkbusterRecieved;
                this.PacketDispatcher.RequestPacketUseMapFunctionRecieved = PacketDispatcher_RequestPacketUseMapFunctionRecieved;
                this.PacketDispatcher.RequestPacketAlterMaplistRecieved = PacketDispatcher_RequestPacketAlterMaplistRecieved;
                this.PacketDispatcher.RequestPacketAdminPlayerMoveRecieved = PacketDispatcher_RequestPacketAdminPlayerMoveRecieved;
                this.PacketDispatcher.RequestPacketAdminPlayerKillRecieved = PacketDispatcher_RequestPacketAdminPlayerKillRecieved;
                this.PacketDispatcher.RequestPacketAdminKickPlayerRecieved = PacketDispatcher_RequestPacketAdminKickPlayerRecieved;
                this.PacketDispatcher.RequestBanListAddRecieved = PacketDispatcher_RequestBanListAddRecieved;
                this.PacketDispatcher.RequestPacketAlterBanListRecieved = PacketDispatcher_RequestPacketAlterBanListRecieved;
                this.PacketDispatcher.RequestPacketAlterReservedSlotsListRecieved = PacketDispatcher_RequestPacketAlterReservedSlotsListRecieved;
                this.PacketDispatcher.RequestPacketAlterTextMonderationListRecieved = PacketDispatcher_RequestPacketAlterTextMonderationListRecieved;
                this.PacketDispatcher.RequestPacketVarsRecieved = PacketDispatcher_RequestPacketVarsRecieved;

                this.PacketDispatcher.RequestPacketSquadLeaderRecieved = PacketDispatcher_RequestPacketSquadLeaderRecieved;
                this.PacketDispatcher.RequestPacketSquadIsPrivateReceived = PacketDispatcher_RequestPacketSquadIsPrivateReceived;
                
            }

            this.ClientShutdown += new LayerClientHandler(CPRoConLayerClient_LayerClientShutdown);

            this.m_praApplication.AccountsList.AccountAdded += new AccountDictionary.AccountAlteredHandler(AccountsList_AccountAdded);
            this.m_praApplication.AccountsList.AccountRemoved += new AccountDictionary.AccountAlteredHandler(AccountsList_AccountRemoved);

            foreach (Account acAccount in this.m_praApplication.AccountsList) {
                this.m_prcClient.Layer.AccountPrivileges[acAccount.Name].AccountPrivilegesChanged += new AccountPrivilege.AccountPrivilegesChangedHandler(CPRoConLayerClient_AccountPrivilegesChanged);
            }

            this.m_prcClient.RecompilingPlugins += new PRoConClient.EmptyParamterHandler(m_prcClient_CompilingPlugins);
            this.m_prcClient.CompilingPlugins += new PRoConClient.EmptyParamterHandler(m_prcClient_CompilingPlugins);

            this.m_prcClient.PassLayerEvent += new PRoConClient.PassLayerEventHandler(m_prcClient_PassLayerEvent);

            if (this.m_prcClient.PluginsManager != null) {
                this.m_prcClient.PluginsManager.PluginLoaded += new PluginManager.PluginEmptyParameterHandler(Plugins_PluginLoaded);
                this.m_prcClient.PluginsManager.PluginEnabled += new PluginManager.PluginEmptyParameterHandler(Plugins_PluginEnabled);
                this.m_prcClient.PluginsManager.PluginDisabled += new PluginManager.PluginEmptyParameterHandler(Plugins_PluginDisabled);
                this.m_prcClient.PluginsManager.PluginVariableAltered += new PluginManager.PluginVariableAlteredHandler(Plugins_PluginVariableAltered);
            }

            this.m_prcClient.MapGeometry.MapZones.MapZoneAdded += new PRoCon.Core.Battlemap.MapZoneDictionary.MapZoneAlteredHandler(MapZones_MapZoneAdded);
            this.m_prcClient.MapGeometry.MapZones.MapZoneChanged += new PRoCon.Core.Battlemap.MapZoneDictionary.MapZoneAlteredHandler(MapZones_MapZoneChanged);
            this.m_prcClient.MapGeometry.MapZones.MapZoneRemoved += new PRoCon.Core.Battlemap.MapZoneDictionary.MapZoneAlteredHandler(MapZones_MapZoneRemoved);

            this.m_prcClient.PluginConsole.WriteConsole += new PRoCon.Core.Logging.Loggable.WriteConsoleHandler(PluginConsole_WriteConsole);
            this.m_prcClient.ChatConsole.WriteConsoleViaCommand += new PRoCon.Core.Logging.Loggable.WriteConsoleHandler(ChatConsole_WriteConsoleViaCommand);

            this.m_prcClient.Variables.VariableAdded += new PRoCon.Core.Variables.VariableDictionary.PlayerAlteredHandler(Variables_VariableAdded);
            this.m_prcClient.Variables.VariableUpdated += new PRoCon.Core.Variables.VariableDictionary.PlayerAlteredHandler(Variables_VariableUpdated);
        }

        private void UnregisterEvents() {

            this.ClientShutdown -= new LayerClientHandler(CPRoConLayerClient_LayerClientShutdown);

            this.m_praApplication.AccountsList.AccountAdded -= new AccountDictionary.AccountAlteredHandler(AccountsList_AccountAdded);
            this.m_praApplication.AccountsList.AccountRemoved -= new AccountDictionary.AccountAlteredHandler(AccountsList_AccountRemoved);

            foreach (Account acAccount in this.m_praApplication.AccountsList) {
                this.m_prcClient.Layer.AccountPrivileges[acAccount.Name].AccountPrivilegesChanged -= new AccountPrivilege.AccountPrivilegesChangedHandler(CPRoConLayerClient_AccountPrivilegesChanged);
            }

            this.m_prcClient.RecompilingPlugins -= new PRoConClient.EmptyParamterHandler(m_prcClient_CompilingPlugins);
            this.m_prcClient.CompilingPlugins -= new PRoConClient.EmptyParamterHandler(m_prcClient_CompilingPlugins);

            this.m_prcClient.PassLayerEvent -= new PRoConClient.PassLayerEventHandler(m_prcClient_PassLayerEvent);

            if (this.m_prcClient.PluginsManager != null) {
                this.m_prcClient.PluginsManager.PluginLoaded -= new PluginManager.PluginEmptyParameterHandler(Plugins_PluginLoaded);
                this.m_prcClient.PluginsManager.PluginEnabled -= new PluginManager.PluginEmptyParameterHandler(Plugins_PluginEnabled);
                this.m_prcClient.PluginsManager.PluginDisabled -= new PluginManager.PluginEmptyParameterHandler(Plugins_PluginDisabled);
                this.m_prcClient.PluginsManager.PluginVariableAltered -= new PluginManager.PluginVariableAlteredHandler(Plugins_PluginVariableAltered);
            }

            this.m_prcClient.MapGeometry.MapZones.MapZoneAdded -= new PRoCon.Core.Battlemap.MapZoneDictionary.MapZoneAlteredHandler(MapZones_MapZoneAdded);
            this.m_prcClient.MapGeometry.MapZones.MapZoneChanged -= new PRoCon.Core.Battlemap.MapZoneDictionary.MapZoneAlteredHandler(MapZones_MapZoneChanged);
            this.m_prcClient.MapGeometry.MapZones.MapZoneRemoved -= new PRoCon.Core.Battlemap.MapZoneDictionary.MapZoneAlteredHandler(MapZones_MapZoneRemoved);

            this.m_prcClient.PluginConsole.WriteConsole -= new PRoCon.Core.Logging.Loggable.WriteConsoleHandler(PluginConsole_WriteConsole);
            this.m_prcClient.ChatConsole.WriteConsoleViaCommand -= new PRoCon.Core.Logging.Loggable.WriteConsoleHandler(ChatConsole_WriteConsoleViaCommand);

            this.m_prcClient.Variables.VariableAdded -= new PRoCon.Core.Variables.VariableDictionary.PlayerAlteredHandler(Variables_VariableAdded);
            this.m_prcClient.Variables.VariableUpdated -= new PRoCon.Core.Variables.VariableDictionary.PlayerAlteredHandler(Variables_VariableUpdated);


        }

        private string m_strClientIPPort = String.Empty;
        public string IPPort {
            get {
                string strClientIPPort = this.m_strClientIPPort;

                // However if the connection is open just get it straight from the horses mouth.
                if (this.PacketDispatcher != null) {
                    strClientIPPort = this.PacketDispatcher.IPPort;
                }

                return strClientIPPort;
            }
        }

        #region Account Authentication

        protected string GeneratePasswordHash(byte[] salt, string data) {
            MD5 md5Hasher = MD5.Create();

            byte[] combined = new byte[salt.Length + data.Length];
            salt.CopyTo(combined, 0);
            Encoding.Default.GetBytes(data).CopyTo(combined, salt.Length);

            byte[] hash = md5Hasher.ComputeHash(combined);

            return hash.Select(x => x.ToString("X2")).Aggregate((a, b) => a + b);
        }

        protected byte[] HashToByteArray(string strHexString) {
            byte[] a_bReturn = new byte[strHexString.Length / 2];

            for (int i = 0; i < a_bReturn.Length; i++) {
                a_bReturn[i] = Convert.ToByte(strHexString.Substring(i * 2, 2), 16);
            }

            return a_bReturn;
        }

        private bool AuthenticatePlaintextAccount(string strUsername, string strPassword) {

            if (String.Compare(this.GetAccountPassword(strUsername), String.Empty) != 0) {
                return (String.Compare(this.GetAccountPassword(strUsername), strPassword) == 0);
            }
            else {
                return false;
            }
        }

        private bool AuthenticateHashedAccount(string strUsername, string strHashedPassword) {
            if (String.Compare(this.GetAccountPassword(strUsername), String.Empty) != 0) {
                return (String.Compare(GeneratePasswordHash(HashToByteArray(this.Salt), this.GetAccountPassword(strUsername)), strHashedPassword) == 0);
            }
            else {
                return false;
            }
        }

        private String GenerateSalt() {
            var provider = new RNGCryptoServiceProvider();
            byte[] buffer = new byte[1024];
            provider.GetBytes(buffer);

            // Note that frostbite sends back a md5 for salt, so we must too.
            this.Salt = this.GeneratePasswordHash(buffer, Convert.ToBase64String(buffer));

            return this.Salt;
        }

        #endregion

        #region Packet Forwarding

        // What we got back from the BFBC2 server..
        UInt32 m_ui32ServerInfoSequenceNumber = 0;
        public void OnServerForwardedResponse(Packet cpPacket) {

            if (this.PacketDispatcher != null) {

                if (this.m_ui32ServerInfoSequenceNumber == cpPacket.SequenceNumber && cpPacket.Words.Count >= 2) {
                    cpPacket.Words[1] = this.m_prcClient.Layer.LayerNameFormat.Replace("%servername%", cpPacket.Words[1]);
                }

                this.PacketDispatcher.SendResponse(cpPacket, cpPacket.Words);

            }

            /*
            if (this.m_connection != null) {

                if (this.m_ui32ServerInfoSequenceNumber == cpPacket.SequenceNumber && cpPacket.Words.Count >= 2) {
                    cpPacket.Words[1] = this.m_prcClient.Layer.LayerNameFormat.Replace("%servername%", cpPacket.Words[1]);
                }

                this.m_connection.SendAsync(cpPacket);
            }
            */
        }

        private void m_prcClient_PassLayerEvent(PRoConClient sender, Packet packet) {

            if (this.PacketDispatcher != null && this.IsLoggedIn == true && this.EventsEnabled == true) {
                this.PacketDispatcher.SendPacket(packet);
            }
            /*
            if (this.m_connection != null && this.m_blEventsEnabled == true) {
                this.m_connection.SendAsync(packet);
            }*/
        }

        #endregion

        #region Packet Handling

        #region Extended Protocol Handling

        #region Procon.Application.Shutdown
        
        // DispatchProconApplicationShutdownRequest
        private void DispatchProconApplicationShutdownRequest(ILayerPacketDispatcher sender, Packet packet)
        {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanShutdownServer == true) {
                    sender.SendResponse(packet, LayerClient.ResponseOk, "but nothing will happen");
                    // shutdowns only the connection not the whole procon... this.m_praApplication.Shutdown();
                } else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            } else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        #endregion

        private void DispatchProconLoginUsernameRequest(ILayerPacketDispatcher sender, Packet packet) {
            this.Username = packet.Words[1];

            // We send back any errors in the login process after they attempt to login.
            if (this.m_praApplication.AccountsList.Contains(this.Username) == true) {
                this.Privileges = this.GetAccountPrivileges(this.Username);

                this.Privileges.SetLowestPrivileges(this.m_prcClient.Privileges);

                if (this.Privileges.CanLogin == true) {
                    sender.SendResponse(packet, LayerClient.ResponseOk);
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseInvalidUsername);
            }
        }

        private void DispatchProconRegisterUidRequest(ILayerPacketDispatcher sender, Packet packet) {
            
            if (this.IsLoggedIn == true) {
            
                bool blEnabled = true;

                if (bool.TryParse(packet.Words[1], out blEnabled) == true) {

                    if (blEnabled == false) {
                        sender.SendResponse(packet, LayerClient.ResponseOk);

                        this.ProconEventsUid = String.Empty;
                    }
                    else if (packet.Words.Count >= 3) {

                        if (this.m_prcClient.Layer.LayerClients.Any(client => client.Value.ProconEventsUid == packet.Words[2]) == false) {
                            sender.SendResponse(packet, LayerClient.ResponseOk);

                            this.ProconEventsUid = packet.Words[2];

                            if (this.UidRegistered != null) {
                                FrostbiteConnection.RaiseEvent(this.UidRegistered.GetInvocationList(), this);
                            }

                        }
                        else {
                            sender.SendResponse(packet, "ProconUidConflict");
                        }
                    }
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInvalidArguments);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void DispatchProconVersionRequest(ILayerPacketDispatcher sender, Packet packet) {
            sender.SendResponse(packet, LayerClient.ResponseOk, Assembly.GetExecutingAssembly().GetName().Version.ToString());
        }

        private void DispatchProconVarsRequest(ILayerPacketDispatcher sender, Packet packet) {

            if (this.IsLoggedIn == true) {

                if (packet.Words.Count == 2) {
                    sender.SendResponse(packet, LayerClient.ResponseOk, packet.Words[1], this.m_prcClient.Variables.GetVariable<string>(packet.Words[1], ""));
                }
                else if (packet.Words.Count > 2) {

                    if (this.Privileges.CanIssueLimitedProconCommands == true) {

                        this.m_prcClient.Variables.SetVariable(packet.Words[1], packet.Words[2]);

                        sender.SendResponse(packet, LayerClient.ResponseOk, packet.Words[1], this.m_prcClient.Variables.GetVariable<string>(packet.Words[1], ""));
                    }
                    else {
                        sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                    }
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInvalidArguments);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void DispatchProconPrivilegesRequest(ILayerPacketDispatcher sender, Packet packet) {

            if (this.IsLoggedIn == true) {
                sender.SendResponse(packet, LayerClient.ResponseOk, this.Privileges.PrivilegesFlags.ToString());
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void DispatchProconCompressionRequest(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {

                bool enableCompress = false;

                if (packet.Words.Count == 2 && bool.TryParse(packet.Words[1], out enableCompress) == true) {
                    this.GzipCompression = enableCompress;
                    
                    sender.SendResponse(packet, LayerClient.ResponseOk);
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInvalidArguments);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void DispatchProconExecRequest(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanIssueAllProconCommands == true) {
                    sender.SendResponse(packet, LayerClient.ResponseOk);

                    packet.Words.RemoveAt(0);
                    this.m_praApplication.ExecutePRoConCommand(this.m_prcClient, packet.Words, 0);
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        #region Accounts

        private void DispatchProconAccountListAccountsRequest(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanIssueLimitedProconCommands == true) {

                    List<string> lstAccounts = new List<string>();
                    lstAccounts.Add(LayerClient.ResponseOk);

                    foreach (string strAccountName in this.m_praApplication.AccountsList.ListAccountNames()) {
                        if (this.m_prcClient.Layer.AccountPrivileges.Contains(strAccountName) == true) {
                            lstAccounts.Add(strAccountName);
                            lstAccounts.Add(this.m_prcClient.Layer.AccountPrivileges[strAccountName].Privileges.PrivilegesFlags.ToString());
                        }
                    }

                    sender.SendResponse(packet, lstAccounts);
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void DispatchProconAccountListLoggedInRequest(ILayerPacketDispatcher sender, Packet packet) {
            if (this.Privileges.CanIssueLimitedProconCommands == true) {

                List<string> lstLoggedInAccounts = this.m_prcClient.Layer.GetLoggedInAccounts((packet.Words.Count >= 2 && String.Compare(packet.Words[1], "uids") == 0));

                //List<string> lstLoggedInAccounts = this.m_prcClient.Layer.GetLoggedInAccounts();
                lstLoggedInAccounts.Insert(0, LayerClient.ResponseOk);

                sender.SendResponse(packet, lstLoggedInAccounts);
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
            }
        }

        private void DispatchProconAccountCreateRequest(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanIssueLimitedProconCommands == true) {

                    if (this.m_praApplication.AccountsList.Contains(packet.Words[1]) == false) {
                        if (packet.Words[2].Length > 0) {
                            sender.SendResponse(packet, LayerClient.ResponseOk);
                            this.m_praApplication.AccountsList.CreateAccount(packet.Words[1], packet.Words[2]);
                            //this.m_uscParent.LayerCreateAccount(
                        }
                        else {
                            sender.SendResponse(packet, LayerClient.ResponseInvalidArguments);
                        }
                    }
                    else {
                        sender.SendResponse(packet, "AccountAlreadyExists");
                    }
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void DispatchProconAccountDeleteRequest(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanIssueLimitedProconCommands == true) {
                    if (packet.Words.Count >= 2) {

                        if (this.m_praApplication.AccountsList.Contains(packet.Words[1]) == true) {
                            sender.SendResponse(packet, LayerClient.ResponseOk);

                            this.m_praApplication.AccountsList.Remove(packet.Words[1]);
                            //this.m_uscParent.LayerDeleteAccount(cpPacket.Words[1]);
                        }
                        else {
                            sender.SendResponse(packet, "AccountDoesNotExists");
                        }
                    }
                    else {
                        sender.SendResponse(packet, LayerClient.ResponseInvalidArguments);
                    }
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void DispatchProconAccountSetPasswordRequest(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanIssueLimitedProconCommands == true) {

                    if (packet.Words.Count >= 3 && packet.Words[2].Length > 0) {

                        if (this.m_praApplication.AccountsList.Contains(packet.Words[1]) == true) {
                            sender.SendResponse(packet, LayerClient.ResponseOk);

                            this.m_praApplication.AccountsList[packet.Words[1]].Password = packet.Words[2];
                        }
                        else {
                            sender.SendResponse(packet, "AccountDoesNotExists");
                        }
                    }
                    else {
                        sender.SendResponse(packet, LayerClient.ResponseInvalidArguments);
                    }
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        #endregion

        #region Battlemap

        private void DispatchProconBattlemapDeleteZoneRequest(ILayerPacketDispatcher sender, Packet packet) {

            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanEditMapZones == true) {
                    if (this.m_prcClient.MapGeometry.MapZones.Contains(packet.Words[1]) == true) {
                        this.m_prcClient.MapGeometry.MapZones.Remove(packet.Words[1]);
                    }

                    sender.SendResponse(packet, LayerClient.ResponseOk);
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void DispatchProconBattlemapCreateZoneRequest(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanEditMapZones == true) {

                    if (packet.Words.Count >= 3) {

                        int iPoints = 0;

                        if (int.TryParse(packet.Words[2], out iPoints) == true) {

                            Point3D[] points = new Point3D[iPoints];

                            for (int i = 0; i < iPoints && i + 3 < packet.Words.Count; i++) {
                                points[i] = new Point3D(packet.Words[2 + i * 3 + 1], packet.Words[2 + i * 3 + 2], packet.Words[2 + i * 3 + 3]);
                            }

                            this.m_prcClient.MapGeometry.MapZones.CreateMapZone(packet.Words[1], points);
                        }

                        sender.SendResponse(packet, LayerClient.ResponseOk);
                    }
                    else {
                        sender.SendResponse(packet, LayerClient.ResponseInvalidArguments);
                    }
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void DispatchProconBattlemapModifyZoneTagsRequest(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanEditMapZones == true) {

                    if (packet.Words.Count >= 3) {

                        if (this.m_prcClient.MapGeometry.MapZones.Contains(packet.Words[1]) == true) {
                            this.m_prcClient.MapGeometry.MapZones[packet.Words[1]].Tags.FromString(packet.Words[2]);
                        }

                        sender.SendResponse(packet, LayerClient.ResponseOk);
                    }
                    else {
                        sender.SendResponse(packet, LayerClient.ResponseInvalidArguments);
                    }
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void DispatchProconBattlemapModifyZonePointsRequest(ILayerPacketDispatcher sender, Packet packet) {
            
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanEditMapZones == true) {

                    if (packet.Words.Count >= 3) {

                        int iPoints = 0;

                        if (int.TryParse(packet.Words[2], out iPoints) == true) {

                            Point3D[] points = new Point3D[iPoints];

                            for (int i = 0; i < iPoints && i + 3 < packet.Words.Count; i++) {
                                points[i] = new Point3D(packet.Words[2 + i * 3 + 1], packet.Words[2 + i * 3 + 2], packet.Words[2 + i * 3 + 3]);
                            }

                            if (this.m_prcClient.MapGeometry.MapZones.Contains(packet.Words[1]) == true) {
                                this.m_prcClient.MapGeometry.MapZones.ModifyMapZonePoints(packet.Words[1], points);
                            }
                        }

                        sender.SendResponse(packet, LayerClient.ResponseOk);
                    }
                    else {
                        sender.SendResponse(packet, LayerClient.ResponseInvalidArguments);
                    }
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void DispatchProconBattlemapListZonesRequest(ILayerPacketDispatcher sender, Packet packet) {

            if (this.IsLoggedIn == true) {

                List<string> listPacket = new List<string>() { LayerClient.ResponseOk };

                listPacket.Add(this.m_prcClient.MapGeometry.MapZones.Count.ToString());

                foreach (MapZoneDrawing zone in this.m_prcClient.MapGeometry.MapZones) {

                    listPacket.Add(zone.UID);
                    listPacket.Add(zone.LevelFileName);
                    listPacket.Add(zone.Tags.ToString());

                    listPacket.Add(zone.ZonePolygon.Length.ToString());
                    listPacket.AddRange(Point3D.ToStringList(zone.ZonePolygon));
                }

                sender.SendResponse(packet, listPacket);
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        #endregion

        #region Layer

        private void DispatchProconLayerSetPrivilegesRequest(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanIssueLimitedProconCommands == true) {

                    UInt32 ui32Privileges = 0;

                    if (packet.Words.Count >= 3 && UInt32.TryParse(packet.Words[2], out ui32Privileges) == true) {

                        if (this.m_praApplication.AccountsList.Contains(packet.Words[1]) == true) {

                            CPrivileges sprvPrivs = new CPrivileges();

                            sender.SendResponse(packet, LayerClient.ResponseOk);

                            sprvPrivs.PrivilegesFlags = ui32Privileges;
                            this.m_prcClient.Layer.AccountPrivileges[packet.Words[1]].SetPrivileges(sprvPrivs);
                        }
                        else {
                            sender.SendResponse(packet, "AccountDoesNotExists");
                        }
                    }
                    else {
                        sender.SendResponse(packet, LayerClient.ResponseInvalidArguments);
                    }
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        #endregion

        #region Plugin

        private void DispatchProconPluginListLoadedRequest(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanIssueLimitedProconPluginCommands == true) {

                    if (packet.Words.Count == 1) {
                        List<string> lstLoadedPlugins = this.GetListLoadedPlugins();

                        lstLoadedPlugins.Insert(0, LayerClient.ResponseOk);

                        sender.SendResponse(packet, lstLoadedPlugins);
                    }
                    else {
                        sender.SendResponse(packet, LayerClient.ResponseInvalidArguments);
                    }
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void DispatchProconPluginListEnabledRequest(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanIssueLimitedProconPluginCommands == true) {
                    List<string> lstEnabledPlugins = this.m_prcClient.PluginsManager.Plugins.EnabledClassNames;
                    lstEnabledPlugins.Insert(0, LayerClient.ResponseOk);

                    sender.SendResponse(packet, lstEnabledPlugins);
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void DispatchProconPluginEnableRequest(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanIssueLimitedProconPluginCommands == true) {
                    bool blEnabled = false;

                    if (packet.Words.Count >= 3 && bool.TryParse(packet.Words[2], out blEnabled) == true) {
                        sender.SendResponse(packet, LayerClient.ResponseOk);

                        if (blEnabled == true) {
                            this.m_prcClient.PluginsManager.EnablePlugin(packet.Words[1]);
                        }
                        else {
                            this.m_prcClient.PluginsManager.DisablePlugin(packet.Words[1]);
                        }
                    }
                    else {
                        sender.SendResponse(packet, LayerClient.ResponseInvalidArguments);
                    }
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void DispatchProconPluginSetVariableRequest(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanIssueLimitedProconPluginCommands == true) {

                    if (packet.Words.Count >= 4) {

                        sender.SendResponse(packet, LayerClient.ResponseOk);

                        this.m_prcClient.PluginsManager.SetPluginVariable(packet.Words[1], packet.Words[2], packet.Words[3]);
                    }
                    else {
                        sender.SendResponse(packet, LayerClient.ResponseInvalidArguments);
                    }
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        #endregion

        #region Communication

        private void DispatchProconAdminSayRequest(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                
                if (packet.Words.Count >= 4) {

                    // Append the admin to the adminstack and send it on its way..
                    if (packet.Words[1].Length > 0) {
                        packet.Words[1] = String.Format("{0}|{1}", packet.Words[1], CPluginVariable.Encode(this.Username));
                    }
                    else {
                        packet.Words[1] = CPluginVariable.Encode(this.Username);
                    }

                    sender.SendResponse(packet, LayerClient.ResponseOk);

                    this.m_prcClient.SendProconLayerPacket(this, packet);
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInvalidArguments);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void DispatchProconAdminYellRequest(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                
                if (packet.Words.Count >= 5) {
                    // Append the admin to the adminstack and send it on its way..
                    if (packet.Words[1].Length > 0) {
                        packet.Words[1] = String.Format("{0}|{1}", packet.Words[1], CPluginVariable.Encode(this.Username));
                    }
                    else {
                        packet.Words[1] = CPluginVariable.Encode(this.Username);
                    }

                    sender.SendResponse(packet, LayerClient.ResponseOk);

                    this.m_prcClient.SendProconLayerPacket(this, packet);
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInvalidArguments);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        #endregion

        private void PacketDispatcher_RequestPacketUnknownRecieved(ILayerPacketDispatcher sender, Packet packet) {

            if (packet.Words.Count >= 1) {
                if (this.m_requestDelegates.ContainsKey(packet.Words[0]) == true) {
                    this.m_requestDelegates[packet.Words[0]](sender, packet);
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseUnknownCommand);
                }
            }
        }

        #endregion

        #region Overridden Protocol Handling

        private void PacketDispatcher_RequestLoginHashed(ILayerPacketDispatcher sender, Packet packet) {
            sender.SendResponse(packet, LayerClient.ResponseOk, this.GenerateSalt());
        }

        private void PacketDispatcher_RequestLoginHashedPassword(ILayerPacketDispatcher sender, Packet packet, string hashedPassword) {

            if (this.m_praApplication.AccountsList.Contains(this.Username) == false) {
                sender.SendResponse(packet, LayerClient.ResponseInvalidUsername);
            }
            else {
                if (this.AuthenticateHashedAccount(this.Username, hashedPassword) == true) {

                    this.Privileges = this.GetAccountPrivileges(this.Username);
                    this.Privileges.SetLowestPrivileges(this.m_prcClient.Privileges);

                    if (this.Privileges.CanLogin == true) {
                        this.IsLoggedIn = true;
                        sender.SendResponse(packet, LayerClient.ResponseOk);

                        if (this.Login != null) {
                            FrostbiteConnection.RaiseEvent(this.Login.GetInvocationList(), this);
                        }
                    }
                    else {
                        sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                    }
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInvalidPasswordHash);
                }
            }
        }

        private void PacketDispatcher_RequestLoginPlainText(ILayerPacketDispatcher sender, Packet packet, string password) {

            if (this.m_praApplication.AccountsList.Contains(this.Username) == false) {
                sender.SendResponse(packet, LayerClient.ResponseInvalidUsername);
            }
            else {

                if (this.AuthenticatePlaintextAccount(this.Username, password) == true) {

                    this.IsLoggedIn = true;
                    sender.SendResponse(packet, LayerClient.ResponseOk);

                    if (this.Login != null) {
                        FrostbiteConnection.RaiseEvent(this.Login.GetInvocationList(), this);
                    }
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInvalidPassword);
                }
            } 
        }

        private void PacketDispatcher_RequestLogout(ILayerPacketDispatcher sender, Packet packet) {
            sender.SendResponse(packet, LayerClient.ResponseOk);
            
            this.IsLoggedIn = false;

            if (this.Logout != null) {
                FrostbiteConnection.RaiseEvent(this.Logout.GetInvocationList(), this);
            }
        }

        private void PacketDispatcher_RequestQuit(ILayerPacketDispatcher sender, Packet packet) {
            sender.SendResponse(packet, LayerClient.ResponseOk);

            if (this.Logout != null) {
                FrostbiteConnection.RaiseEvent(this.Logout.GetInvocationList(), this);
            }

            if (this.Quit != null) {
                FrostbiteConnection.RaiseEvent(this.Quit.GetInvocationList(), this);
            }

            this.Shutdown();
        }

        private void PacketDispatcher_RequestEventsEnabled(ILayerPacketDispatcher sender, Packet packet, bool eventsEnabled) {
            if (this.IsLoggedIn == true) {
                sender.SendResponse(packet, LayerClient.ResponseOk);

                this.EventsEnabled = eventsEnabled;
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void PacketDispatcher_RequestHelp(ILayerPacketDispatcher sender, Packet packet) {
            // TO DO: Edit on way back with additional commands IF NOT PRESENT.
            this.m_prcClient.SendProconLayerPacket(this, packet);
        }

        private void PacketDispatcher_RequestPacketAdminShutdown(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanShutdownServer == true) {
                    this.m_prcClient.SendProconLayerPacket(this, packet);
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        #endregion

        #region PacketDispatcher Protocol Handling

        private void PacketDispatcher_RequestPacketSecureSafeListedRecieved(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                this.m_prcClient.SendProconLayerPacket(this, packet);
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void PacketDispatcher_RequestPacketUnsecureSafeListedRecieved(ILayerPacketDispatcher sender, Packet packet) {
            
            if (packet.Words.Count >= 1 && String.Compare(packet.Words[0], "serverInfo", true) == 0) {
                this.m_ui32ServerInfoSequenceNumber = packet.SequenceNumber;
            }
            
            this.m_prcClient.SendProconLayerPacket(this, packet);
        }

        private void PacketDispatcher_RequestPacketPunkbusterRecieved(ILayerPacketDispatcher sender, Packet packet) {
 	        if (this.IsLoggedIn == true) {

                if (packet.Words.Count >= 2) {
                    
                    bool blCommandProcessed = false;
                    
                    if (this.Privileges.CannotIssuePunkbusterCommands == true) {
                        sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);

                        blCommandProcessed = true;
                    }
                    else {
                        Match mtcMatch = Regex.Match(packet.Words[1], "^(?=(?<pb_sv_command>pb_sv_plist))|(?=(?<pb_sv_command>pb_sv_ban))|(?=(?<pb_sv_command>pb_sv_banguid))|(?=(?<pb_sv_command>pb_sv_banlist))|(?=(?<pb_sv_command>pb_sv_getss))|(?=(?<pb_sv_command>pb_sv_kick)[ ]+?.*?[ ]+?(?<pb_sv_kick_time>[0-9]+)[ ]+)|(?=(?<pb_sv_command>pb_sv_unban))|(?=(?<pb_sv_command>pb_sv_unbanguid))|(?=(?<pb_sv_command>pb_sv_reban))", RegexOptions.IgnoreCase);

                        // IF they tried to issue a pb_sv_command that isn't on the safe list AND they don't have full access.
                        if (mtcMatch.Success == false && this.Privileges.CanIssueAllPunkbusterCommands == false) {
                            sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                            blCommandProcessed = true;
                        }
                        else {

                            if (this.Privileges.CanPermanentlyBanPlayers == false && (String.Compare(mtcMatch.Groups["pb_sv_command"].Value, "pb_sv_ban", true) == 0 || String.Compare(mtcMatch.Groups["pb_sv_command"].Value, "pb_sv_banguid", true) == 0 || String.Compare(mtcMatch.Groups["pb_sv_command"].Value, "pb_sv_reban", true) == 0)) {
                                sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                                blCommandProcessed = true;
                            }
                            else if (this.Privileges.CanEditBanList == false && (String.Compare(mtcMatch.Groups["pb_sv_command"].Value, "pb_sv_unban", true) == 0 || String.Compare(mtcMatch.Groups["pb_sv_command"].Value, "pb_sv_unbanguid", true) == 0)) {
                                sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                                blCommandProcessed = true;
                            }
                            else if (String.Compare(mtcMatch.Groups["pb_sv_command"].Value, "pb_sv_kick", true) == 0) {

                                int iBanLength = 0;

                                // NOTE* Punkbuster uses minutes not seconds.
                                if (int.TryParse(mtcMatch.Groups["pb_sv_kick_time"].Value, out iBanLength) == true) {

                                    // If they cannot punish players at all..
                                    if (this.Privileges.CannotPunishPlayers == true) {
                                        sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                                        blCommandProcessed = true;
                                    }
                                    // If they can temporary ban but not permanently ban BUT the banlength is over an hour (default)
                                    else if (this.Privileges.CanTemporaryBanPlayers == true && this.Privileges.CanPermanentlyBanPlayers == false && iBanLength > (this.m_prcClient.Variables.GetVariable<int>("TEMP_BAN_CEILING", 3600) / 60)) {
                                        sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                                        blCommandProcessed = true;
                                    }
                                    // If they can kick but not temp or perm ban players AND the banlength is over 0 (no ban time)
                                    else if (this.Privileges.CanKickPlayers == true && this.Privileges.CanTemporaryBanPlayers == false && this.Privileges.CanPermanentlyBanPlayers == false && iBanLength > 0) {
                                        sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                                        blCommandProcessed = true;
                                    }
                                    // ELSE they have punkbuster access and full ban privs.. issue the command.
                                }
                                else { // Would rather stop it here than pass it on
                                    sender.SendResponse(packet, LayerClient.ResponseInvalidArguments);

                                    blCommandProcessed = true;
                                }
                            }
                            // ELSE they have permission to issue this command (full or partial)
                        }
                    }

                    // Was not denied above, send it on to the PacketDispatcher server.
                    if (blCommandProcessed == false) {
                        this.m_prcClient.SendProconLayerPacket(this, packet);
                    }
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInvalidArguments);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void PacketDispatcher_RequestPacketUseMapFunctionRecieved(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanUseMapFunctions == true) {
                    this.m_prcClient.SendProconLayerPacket(this, packet);
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void PacketDispatcher_RequestPacketAlterMaplistRecieved(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanEditMapList == true) {
                    this.m_prcClient.SendProconLayerPacket(this, packet);
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void PacketDispatcher_RequestPacketAdminPlayerMoveRecieved(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanMovePlayers == true) {
                    this.m_prcClient.SendProconLayerPacket(this, packet);
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void PacketDispatcher_RequestPacketAdminPlayerKillRecieved(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanKillPlayers == true) {
                    this.m_prcClient.SendProconLayerPacket(this, packet);
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void PacketDispatcher_RequestPacketAdminKickPlayerRecieved(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanKickPlayers == true) {
                    this.m_prcClient.SendProconLayerPacket(this, packet);
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void PacketDispatcher_RequestBanListAddRecieved(ILayerPacketDispatcher sender, Packet packet, CBanInfo newBan) {
            if (this.IsLoggedIn == true) {

                if (newBan.BanLength.Subset == TimeoutSubset.TimeoutSubsetType.Permanent && this.Privileges.CanPermanentlyBanPlayers == true) {
                    this.m_prcClient.SendProconLayerPacket(this, packet);
                }
                else if (newBan.BanLength.Subset == TimeoutSubset.TimeoutSubsetType.Round && this.Privileges.CanTemporaryBanPlayers == true) {
                    this.m_prcClient.SendProconLayerPacket(this, packet);
                }
                else if (newBan.BanLength.Subset == TimeoutSubset.TimeoutSubsetType.Seconds && this.Privileges.CanPermanentlyBanPlayers == true) {
                    this.m_prcClient.SendProconLayerPacket(this, packet);
                }
                else if (newBan.BanLength.Subset == TimeoutSubset.TimeoutSubsetType.Seconds && this.Privileges.CanTemporaryBanPlayers == true) {
                    
                    if (newBan.BanLength.Seconds <= this.m_prcClient.Variables.GetVariable<int>("TEMP_BAN_CEILING", 3600)) {
                        this.m_prcClient.SendProconLayerPacket(this, packet);
                    }
                    else {
                        sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                    }
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void PacketDispatcher_RequestPacketAlterBanListRecieved(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanEditBanList == true) {
                    this.m_prcClient.SendProconLayerPacket(this, packet);
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void PacketDispatcher_RequestPacketAlterTextMonderationListRecieved(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanEditTextChatModerationList == true) {
                    this.m_prcClient.SendProconLayerPacket(this, packet);
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void PacketDispatcher_RequestPacketAlterReservedSlotsListRecieved(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanEditReservedSlotsList == true) {
                    this.m_prcClient.SendProconLayerPacket(this, packet);
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void PacketDispatcher_RequestPacketVarsRecieved(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanAlterServerSettings == true) {
                    this.m_prcClient.SendProconLayerPacket(this, packet);
                }
                else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            }
            else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        #endregion

        #endregion

        #region player/squad cmds

        private void PacketDispatcher_RequestPacketSquadLeaderRecieved(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanMovePlayers == true) {
                    this.m_prcClient.SendProconLayerPacket(this, packet);
                } else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            } else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }

        private void PacketDispatcher_RequestPacketSquadIsPrivateReceived(ILayerPacketDispatcher sender, Packet packet) {
            if (this.IsLoggedIn == true) {
                if (this.Privileges.CanMovePlayers == true) {
                    this.m_prcClient.SendProconLayerPacket(this, packet);
                } else {
                    sender.SendResponse(packet, LayerClient.ResponseInsufficientPrivileges);
                }
            } else {
                sender.SendResponse(packet, LayerClient.ResponseLoginRequired);
            }
        }


        #endregion

        #region Accounts


        private string GetAccountPassword(string strUsername) {

            string strReturnPassword = String.Empty;

            if (this.m_praApplication.AccountsList.Contains(strUsername) == true) {
                strReturnPassword = this.m_praApplication.AccountsList[strUsername].Password;
            }

            if (String.IsNullOrEmpty(strUsername) == true) {
                strReturnPassword = this.m_prcClient.Variables.GetVariable<string>("GUEST_PASSWORD", "");
            }

            return strReturnPassword;
        }

        private CPrivileges GetAccountPrivileges(string strUsername) {

            CPrivileges sprReturn = new CPrivileges();
            sprReturn.PrivilegesFlags = 0;

            if (this.m_prcClient.Layer.AccountPrivileges.Contains(strUsername) == true) {
                sprReturn = this.m_prcClient.Layer.AccountPrivileges[strUsername].Privileges;
                //sprReturn = this.m_praApplication.AccountsList[strUsername].AccountPrivileges[this.m_prcClient.HostNamePort].Privileges;
            }

            if (String.IsNullOrEmpty(strUsername) == true && this.m_prcClient.Variables.IsVariableNullOrEmpty("GUEST_PRIVILEGES") == false) {
                sprReturn.PrivilegesFlags = this.m_prcClient.Variables.GetVariable<UInt32>("GUEST_PRIVILEGES", 0);
            }

            return sprReturn;
        }

        // TO DO: Implement event once available
        public void OnAccountLogin(string strUsername, CPrivileges sprvPrivileges) {
            if (this.IsLoggedIn == true && this.EventsEnabled == true && this.PacketDispatcher != null) {
                this.PacketDispatcher.SendRequest("procon.account.onLogin", strUsername, sprvPrivileges.PrivilegesFlags.ToString());
                //this.send(new Packet(true, false, this.AcquireSequenceNumber, new List<string>() { "procon.account.onLogin", strUsername, sprvPrivileges.PrivilegesFlags.ToString() }));
            }
        }

        // TO DO: Implement event once available
        public void OnAccountLogout(string strUsername) {
            if (this.IsLoggedIn == true && String.Compare(strUsername, this.Username) != 0 && this.EventsEnabled == true && this.PacketDispatcher != null) {
                this.PacketDispatcher.SendRequest("procon.account.onLogout", strUsername);
                //this.send(new Packet(true, false, this.AcquireSequenceNumber, new List<string>() { "procon.account.onLogout", strUsername }));
            }
        }

        private void CPRoConLayerClient_AccountPrivilegesChanged(AccountPrivilege item) {

            CPrivileges cpPrivs = new CPrivileges(item.Privileges.PrivilegesFlags);

            cpPrivs.SetLowestPrivileges(this.m_prcClient.Privileges);

            if (this.IsLoggedIn == true && this.EventsEnabled == true && this.PacketDispatcher != null) {
                this.PacketDispatcher.SendRequest("procon.account.onAltered", item.Owner.Name, cpPrivs.PrivilegesFlags.ToString());
                //this.send(new Packet(true, false, this.AcquireSequenceNumber, new List<string>() { "procon.account.onAltered", item.Owner.Name, cpPrivs.PrivilegesFlags.ToString() }));
            }

            if (String.Compare(this.Username, item.Owner.Name) == 0) {
                this.Privileges = cpPrivs;
            }
        }

        private void AccountsList_AccountRemoved(Account item) {

            if (this.IsLoggedIn == true && this.EventsEnabled == true && this.PacketDispatcher != null) {
                this.PacketDispatcher.SendRequest("procon.account.onDeleted", item.Name);
                //this.send(new Packet(true, false, this.AcquireSequenceNumber, new List<string>() { "procon.account.onDeleted", item.Name }));
            }
        }

        private void AccountsList_AccountAdded(Account item) {

            this.m_prcClient.Layer.AccountPrivileges[item.Name].AccountPrivilegesChanged += new AccountPrivilege.AccountPrivilegesChangedHandler(CPRoConLayerClient_AccountPrivilegesChanged);

            if (this.IsLoggedIn == true && this.EventsEnabled == true && this.PacketDispatcher != null) {
                this.PacketDispatcher.SendRequest("procon.account.onCreated", item.Name);
                //this.send(new Packet(true, false, this.AcquireSequenceNumber, new List<string>() { "procon.account.onCreated", item.Name }));
            }
        }

        public void OnRegisteredUid(string uid, string strUsername) {
            if (this.IsLoggedIn == true && this.EventsEnabled == true && this.PacketDispatcher != null) {
                this.PacketDispatcher.SendRequest("procon.account.onUidRegistered", uid, strUsername);
                //this.send(new Packet(true, false, this.AcquireSequenceNumber, new List<string>() { "procon.account.onLogin", strUsername, sprvPrivileges.PrivilegesFlags.ToString() }));
            }
        }

        #endregion

        #region Plugins

        private List<string> GetListLoadedPlugins() {
            List<string> lstReturn = new List<string>();

            foreach (string strPluginClassName in this.m_prcClient.PluginsManager.Plugins.LoadedClassNames) {
                // Get some updated plugin details..
                PluginDetails spDetails = this.m_prcClient.PluginsManager.GetPluginDetails(strPluginClassName);

                lstReturn.Add(spDetails.ClassName);

                lstReturn.Add(spDetails.Name);
                lstReturn.Add(spDetails.Author);
                lstReturn.Add(spDetails.Website);
                lstReturn.Add(spDetails.Version);
                if (this.GzipCompression == true) {
                    lstReturn.Add(Packet.Compress(spDetails.Description));
                }
                else {
                    lstReturn.Add(spDetails.Description);
                }
                lstReturn.Add(spDetails.DisplayPluginVariables.Count.ToString());

                foreach (CPluginVariable cpvVariable in spDetails.DisplayPluginVariables) {
                    lstReturn.Add(cpvVariable.Name);
                    lstReturn.Add(cpvVariable.Type);
                    lstReturn.Add(cpvVariable.Value);
                }
            }

            return lstReturn;
        }

        private void m_prcClient_CompilingPlugins(PRoConClient sender) {
            this.m_prcClient.PluginsManager.PluginLoaded += new PluginManager.PluginEmptyParameterHandler(Plugins_PluginLoaded);
            this.m_prcClient.PluginsManager.PluginEnabled += new PluginManager.PluginEmptyParameterHandler(Plugins_PluginEnabled);
            this.m_prcClient.PluginsManager.PluginDisabled += new PluginManager.PluginEmptyParameterHandler(Plugins_PluginDisabled);
            this.m_prcClient.PluginsManager.PluginVariableAltered += new PluginManager.PluginVariableAlteredHandler(Plugins_PluginVariableAltered);
        }

        private void Plugins_PluginLoaded(string strClassName) {
            PluginDetails spdDetails = this.m_prcClient.PluginsManager.GetPluginDetails(strClassName);

            if (this.IsLoggedIn == true && this.EventsEnabled == true && this.PacketDispatcher != null) {

                List<string> lstOnPluginLoaded = new List<string>() { "procon.plugin.onLoaded", spdDetails.ClassName, spdDetails.Name, spdDetails.Author, spdDetails.Website, spdDetails.Version, spdDetails.Description, spdDetails.DisplayPluginVariables.Count.ToString() };

                foreach (CPluginVariable cpvVariable in spdDetails.DisplayPluginVariables) {
                    lstOnPluginLoaded.AddRange(new List<string> { cpvVariable.Name, cpvVariable.Type, cpvVariable.Value });
                }

                this.PacketDispatcher.SendRequest(lstOnPluginLoaded);
                //this.send(new Packet(true, false, this.AcquireSequenceNumber, lstOnPluginLoaded));
            }
        }

        private void Plugins_PluginVariableAltered(PluginDetails spdNewDetails) {
            if (this.IsLoggedIn == true && this.EventsEnabled == true && this.PacketDispatcher != null) {

                List<string> lstWords = new List<string>() { "procon.plugin.onVariablesAltered", spdNewDetails.ClassName, (spdNewDetails.DisplayPluginVariables.Count).ToString() };

                foreach (CPluginVariable cpvVariable in spdNewDetails.DisplayPluginVariables) {
                    lstWords.AddRange(new string[] { cpvVariable.Name, cpvVariable.Type, cpvVariable.Value });
                }

                this.PacketDispatcher.SendRequest(lstWords);
                //this.send(new Packet(true, false, this.AcquireSequenceNumber, lstWords));
            }
        }

        private void Plugins_PluginEnabled(string strClassName) {
            if (this.IsLoggedIn == true && this.EventsEnabled == true && this.PacketDispatcher != null) {
                this.PacketDispatcher.SendRequest("procon.plugin.onEnabled", strClassName, Packet.Bltos(true));
                //this.send(new Packet(true, false, this.AcquireSequenceNumber, new List<string>() { "procon.plugin.onEnabled", strClassName, bool.TrueString }));
            }
        }

        private void Plugins_PluginDisabled(string strClassName) {
            if (this.IsLoggedIn == true && this.EventsEnabled == true && this.PacketDispatcher != null) {
                this.PacketDispatcher.SendRequest("procon.plugin.onEnabled", strClassName, Packet.Bltos(false));
                //this.send(new Packet(true, false, this.AcquireSequenceNumber, new List<string>() { "procon.plugin.onEnabled", strClassName, bool.FalseString }));
            }
        }

        public void PluginConsole_WriteConsole(DateTime dtLoggedTime, string strLoggedText) {
            if (this.IsLoggedIn == true && this.EventsEnabled == true && this.PacketDispatcher != null) {
                this.PacketDispatcher.SendRequest("procon.plugin.onConsole", dtLoggedTime.ToBinary().ToString(), strLoggedText);
                //this.send(new Packet(true, false, this.AcquireSequenceNumber, new List<string>() { "procon.plugin.onConsole", dtLoggedTime.ToBinary().ToString(), strLoggedText }));
            }
        }

        #endregion

        #region Chat

        public void ChatConsole_WriteConsoleViaCommand(DateTime dtLoggedTime, string strLoggedText) {
            if (this.IsLoggedIn == true && this.EventsEnabled == true && this.PacketDispatcher != null) {
                this.PacketDispatcher.SendRequest("procon.chat.onConsole", dtLoggedTime.ToBinary().ToString(), strLoggedText);
                //this.send(new Packet(true, false, this.AcquireSequenceNumber, new List<string>() { "procon.chat.onConsole", dtLoggedTime.ToBinary().ToString(), strLoggedText }));
            }
        }

        #endregion

        #region Map Zones

        private void MapZones_MapZoneRemoved(PRoCon.Core.Battlemap.MapZoneDrawing item) {
            if (this.IsLoggedIn == true && this.EventsEnabled == true && this.PacketDispatcher != null) {
                this.PacketDispatcher.SendRequest("procon.battlemap.onZoneRemoved", item.UID);
                //this.send(new Packet(true, false, this.AcquireSequenceNumber, new List<string>() { "procon.battlemap.onZoneRemoved", item.UID }));
            }
        }

        private void MapZones_MapZoneChanged(PRoCon.Core.Battlemap.MapZoneDrawing item) {
            if (this.IsLoggedIn == true && this.EventsEnabled == true && this.PacketDispatcher != null) {
                List<string> packet = new List<string>() { "procon.battlemap.onZoneModified", item.UID, item.Tags.ToString(), item.ZonePolygon.Length.ToString() };

                packet.AddRange(Point3D.ToStringList(item.ZonePolygon));

                this.PacketDispatcher.SendRequest(packet);
                //this.send(new Packet(true, false, this.AcquireSequenceNumber, packet));
            }
        }

        private void MapZones_MapZoneAdded(PRoCon.Core.Battlemap.MapZoneDrawing item) {
            if (this.IsLoggedIn == true && this.EventsEnabled == true && this.PacketDispatcher != null) {
                List<string> packet = new List<string>() { "procon.battlemap.onZoneCreated", item.UID, item.LevelFileName, item.ZonePolygon.Length.ToString() };

                packet.AddRange(Point3D.ToStringList(item.ZonePolygon));

                this.PacketDispatcher.SendRequest(packet);
                //this.send(new Packet(true, false, this.AcquireSequenceNumber, packet));
            }
        }

        #endregion

        #region Variables

        private void Variables_VariableUpdated(PRoCon.Core.Variables.Variable item) {
            if (this.IsLoggedIn == true && this.EventsEnabled == true && this.PacketDispatcher != null) {
                this.PacketDispatcher.SendRequest("procon.vars.onAltered", item.Name, item.Value);
                //this.send(new Packet(true, false, this.AcquireSequenceNumber, new List<string>() { "procon.vars.onAltered", item.Name, item.Value }));
            }
        }

        private void Variables_VariableAdded(PRoCon.Core.Variables.Variable item) {
            if (this.IsLoggedIn == true && this.EventsEnabled == true && this.PacketDispatcher != null) {
                this.PacketDispatcher.SendRequest("procon.vars.onAltered", item.Name, item.Value);
                //this.send(new Packet(true, false, this.AcquireSequenceNumber, new List<string>() { "procon.vars.onAltered", item.Name, item.Value }));
            }
        }

        #endregion

        private void PacketDispatcher_ConnectionClosed(ILayerPacketDispatcher sender) {
            if (this.ClientShutdown != null) {
                FrostbiteConnection.RaiseEvent(this.ClientShutdown.GetInvocationList(), this);
            }
        }

        private void CPRoConLayerClient_LayerClientShutdown(LayerClient sender) {
            this.UnregisterEvents();
        }

        // TODO: Change to event once this.m_prcClient.Layer has shutdown event..
        public void OnShutdown() {
            if (this.PacketDispatcher != null) {
                this.PacketDispatcher.SendRequest("procon.shutdown");
            }
        }

        public void Shutdown() {
            if (this.PacketDispatcher != null) {
                this.PacketDispatcher.Shutdown();
            }
        }
    }
}
