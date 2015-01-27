using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Threading;
using Ibt.Ortc.Api.Extensibility;
using Ibt.Ortc.Plugin.IbtRealTimeSJ.WS;

namespace Ibt.Ortc.Plugin.IbtRealTimeSJ
{
    /// <summary>
    /// IBT Real Time SJ type client.
    /// </summary>
    public class IbtRealTimeSJClient : OrtcClient
    {
        #region Constants (11)

        // REGEX patterns
        private const string OPERATION_PATTERN = @"^a\[""{""op"":""(?<op>[^\""]+)"",(?<args>.*)}""\]$";
        private const string CLOSE_PATTERN = @"^c\[?(?<code>[^""]+),?""?(?<message>.*)""?\]?$";
        private const string VALIDATED_PATTERN = @"^(""up"":){1}(?<up>.*)?,""set"":(?<set>.*)$";
        private const string CHANNEL_PATTERN = @"^""ch"":""(?<channel>.*)""$";
        private const string EXCEPTION_PATTERN = @"^""ex"":{(""op"":""(?<op>[^""]+)"",)?(""ch"":""(?<channel>.*)"",)?""ex"":""(?<error>.*)""}$";
        private const string RECEIVED_PATTERN = @"^a\[""{""ch"":""(?<channel>.*)"",""m"":""(?<message>[\s\S]*)""}""\]$";
        private const string MULTI_PART_MESSAGE_PATTERN = @"^(?<messageId>.[^_]*)_(?<messageCurrentPart>.[^-]*)-(?<messageTotalPart>.[^_]*)_(?<message>[\s\S]*)$";
        private const string PERMISSIONS_PATTERN = @"""(?<key>[^""]+)"":{1}""(?<value>[^,""]+)"",?";
        private const string CLUSTER_RESPONSE_PATTERN = @"var SOCKET_SERVER = ""(?<host>.*)"";";

        #endregion

        #region Attributes (14)

        private string _applicationKey;
        private string _authenticationToken;
        private bool _isConnecting;
        private bool _alreadyConnectedFirstTime;
        private bool _stopReconnecting;
        private bool _callDisconnectedCallback;
        private bool _waitingServerResponse;
        private List<KeyValuePair<string, string>> _permissions;
        private ConcurrentDictionary<string, ChannelSubscription> _subscribedChannels;
        private ConcurrentDictionary<string, ConcurrentDictionary<int, BufferedMessage>> _multiPartMessagesBuffer;
        private WebSocketConnection _webSocketConnection;
        private DateTime? _reconnectStartedAt;
        private DateTime? _lastKeepAlive; // Holds the time of the last keep alive received from the server
        private SynchronizationContext _synchContext; // To synchronize different contexts, preventing cross-thread operation errors (Windows Application and WPF Application))
        private System.Timers.Timer _reconnectTimer; // Timer to reconnect
        private int _sessionExpirationTime; // minutes
        private System.Timers.Timer _heartbeatTimer; 

        #endregion

        #region Constructor (1)

        /// <summary>
        /// Initializes a new instance of the <see cref="IbtRealTimeSClient"/> class.
        /// </summary>
        public IbtRealTimeSJClient()
        {
            ConnectionTimeout = 5000;
            _sessionExpirationTime = 30;

            IsConnected = false;
            IsCluster = false;
            _isConnecting = false;
            _alreadyConnectedFirstTime = false;
            _stopReconnecting = false;
            _callDisconnectedCallback = false;
            _waitingServerResponse = false;

            HeartbeatActive = false;
            HeartbeatFails = 3;
            HeartbeatTime = 15;
            _heartbeatTimer = new System.Timers.Timer();
            _heartbeatTimer.Elapsed += new ElapsedEventHandler(_heartbeatTimer_Elapsed);

            _permissions = new List<KeyValuePair<string, string>>();

            _lastKeepAlive = null;
            _reconnectStartedAt = null;
            _reconnectTimer = null;

            _subscribedChannels = new ConcurrentDictionary<string, ChannelSubscription>();
            _multiPartMessagesBuffer = new ConcurrentDictionary<string, ConcurrentDictionary<int, BufferedMessage>>();

            // To catch unobserved exceptions
            TaskScheduler.UnobservedTaskException += new EventHandler<UnobservedTaskExceptionEventArgs>(TaskScheduler_UnobservedTaskException);

            // To use the same context inside the tasks and prevent cross-thread operation errors (Windows Application and WPF Application)
            _synchContext = System.Threading.SynchronizationContext.Current;

            _webSocketConnection = new WebSocketConnection();

            _webSocketConnection.OnOpened += new WebSocketConnection.onOpenedDelegate(_webSocketConnection_OnOpened);
            _webSocketConnection.OnClosed += new WebSocketConnection.onClosedDelegate(_webSocketConnection_OnClosed);
            _webSocketConnection.OnError += new WebSocketConnection.onErrorDelegate(_webSocketConnection_OnError);
            _webSocketConnection.OnMessageReceived += new WebSocketConnection.onMessageReceivedDelegate(_webSocketConnection_OnMessageReceived);
        }

        #endregion

        #region Public Methods (6)

        /// <summary>
        /// Connects to the gateway with the application key and authentication token. The gateway must be set before using this method.
        /// </summary>
        /// <param name="appKey">Your application key to use ORTC.</param>
        /// <param name="authToken">Authentication token that identifies your permissions.</param>
        /// <example>
        ///   <code>
        /// ortcClient.Connect("myApplicationKey", "myAuthenticationToken");
        ///   </code>
        ///   </example>
        public override void Connect(string appKey, string authToken)
        {
            #region Sanity Checks

            if (IsConnected)
            {
                DelegateExceptionCallback(new OrtcAlreadyConnectedException("Already connected"));
            }
            else if (String.IsNullOrEmpty(ClusterUrl) && String.IsNullOrEmpty(Url))
            {
                DelegateExceptionCallback(new OrtcEmptyFieldException("URL and Cluster URL are null or empty"));
            }
            else if (String.IsNullOrEmpty(appKey))
            {
                DelegateExceptionCallback(new OrtcEmptyFieldException("Application Key is null or empty"));
            }
            else if (String.IsNullOrEmpty(authToken))
            {
                DelegateExceptionCallback(new OrtcEmptyFieldException("Authentication ToKen is null or empty"));
            }
            else if (!IsCluster && !Url.OrtcIsValidUrl())
            {
                DelegateExceptionCallback(new OrtcInvalidCharactersException("Invalid URL"));
            }
            else if (IsCluster && !ClusterUrl.OrtcIsValidUrl())
            {
                DelegateExceptionCallback(new OrtcInvalidCharactersException("Invalid Cluster URL"));
            }
            else if (!appKey.OrtcIsValidInput())
            {
                DelegateExceptionCallback(new OrtcInvalidCharactersException("Application Key has invalid characters"));
            }
            else if (!authToken.OrtcIsValidInput())
            {
                DelegateExceptionCallback(new OrtcInvalidCharactersException("Authentication Token has invalid characters"));
            }
            else if (AnnouncementSubChannel != null && !AnnouncementSubChannel.OrtcIsValidInput())
            {
                DelegateExceptionCallback(new OrtcInvalidCharactersException("Announcement Subchannel has invalid characters"));
            }
            else if (!String.IsNullOrEmpty(ConnectionMetadata) && ConnectionMetadata.Length > MAX_CONNECTION_METADATA_SIZE)
            {
                DelegateExceptionCallback(new OrtcMaxLengthException(String.Format("Connection metadata size exceeds the limit of {0} characters", MAX_CONNECTION_METADATA_SIZE)));
            }
            else if (_isConnecting || _reconnectStartedAt != null)
            {
                DelegateExceptionCallback(new OrtcAlreadyConnectedException("Already trying to connect"));
            }
            else

            #endregion
            {
                _stopReconnecting = false;

                _authenticationToken = authToken;
                _applicationKey = appKey;

                DoConnect();
            }
        }

        /// <summary>
        /// Sends a message to a channel.
        /// </summary>
        /// <param name="channel">Channel name.</param>
        /// <param name="message">Message to be sent.</param>
        /// <example>
        ///   <code>
        /// ortcClient.Send("channelName", "messageToSend");
        ///   </code>
        ///   </example>
        public override void Send(string channel, string message)
        {
            #region Sanity Checks

            if (!IsConnected)
            {
                DelegateExceptionCallback(new OrtcNotConnectedException("Not connected"));
            }
            else if (String.IsNullOrEmpty(channel))
            {
                DelegateExceptionCallback(new OrtcEmptyFieldException("Channel is null or empty"));
            }
            else if (!channel.OrtcIsValidInput())
            {
                DelegateExceptionCallback(new OrtcInvalidCharactersException("Channel has invalid characters"));
            }
            else if (String.IsNullOrEmpty(message))
            {
                DelegateExceptionCallback(new OrtcEmptyFieldException("Message is null or empty"));
            }
            else

            #endregion
            {
                byte[] channelBytes = Encoding.UTF8.GetBytes(channel);

                if (channelBytes.Length > MAX_CHANNEL_SIZE)
                {
                    DelegateExceptionCallback(new OrtcMaxLengthException(String.Format("Channel size exceeds the limit of {0} characters", MAX_CHANNEL_SIZE)));
                }
                else
                {
                    var domainChannelCharacterIndex = channel.IndexOf(':');
                    var channelToValidate = channel;

                    if (domainChannelCharacterIndex > 0)
                    {
                        channelToValidate = channel.Substring(0, domainChannelCharacterIndex + 1) + "*";
                    }

                    string hash = _permissions.Where(c => c.Key == channel || c.Key == channelToValidate).FirstOrDefault().Value;

                    if (_permissions != null && _permissions.Count > 0 && String.IsNullOrEmpty(hash))
                    {
                        DelegateExceptionCallback(new OrtcNotConnectedException(String.Format("No permission found to send to the channel '{0}'", channel)));
                    }
                    else
                    {
                        message = message.Replace(Environment.NewLine, "\n");

                        if (channel != String.Empty && message != String.Empty)
                        {
                            try
                            {
                                byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                                ArrayList messageParts = new ArrayList();
                                int pos = 0;
                                int remaining;
                                string messageId = Strings.GenerateId(8);

                                // Multi part
                                while ((remaining = messageBytes.Length - pos) > 0)
                                {
                                    byte[] messagePart;

                                    if (remaining >= MAX_MESSAGE_SIZE - channelBytes.Length)
                                    {
                                        messagePart = new byte[MAX_MESSAGE_SIZE - channelBytes.Length];
                                    }
                                    else
                                    {
                                        messagePart = new byte[remaining];
                                    }

                                    Array.Copy(messageBytes, pos, messagePart, 0, messagePart.Length);

                                    messageParts.Add(Encoding.UTF8.GetString((byte[])messagePart));

                                    pos += messagePart.Length;
                                }

                                for (int i = 0; i < messageParts.Count; i++)
                                {
                                    string s = String.Format("send;{0};{1};{2};{3};{4}", _applicationKey, _authenticationToken, channel, hash, String.Format("{0}_{1}-{2}_{3}", messageId, i + 1, messageParts.Count, messageParts[i]));

                                    DoSend(s);
                                }
                            }
                            catch (Exception ex)
                            {
                                string exName = null;

                                if (ex.InnerException != null)
                                {
                                    exName = ex.InnerException.GetType().Name;
                                }

                                switch (exName)
                                {
                                    case "OrtcNotConnectedException":
                                        // Server went down
                                        if (IsConnected)
                                        {
                                            DoDisconnect();
                                        }
                                        break;
                                    default:
                                        DelegateExceptionCallback(new OrtcGenericException(String.Format("Unable to send: {0}", ex)));
                                        break;
                                }
                            }
                        }
                    }
                }
            }
        }

        public override void SendProxy(string applicationKey, string privateKey, string channel, string message)
        {
            #region Sanity Checks

            if (!IsConnected)
            {
                DelegateExceptionCallback(new OrtcNotConnectedException("Not connected"));
            }
            else if (String.IsNullOrEmpty(applicationKey))
            {
                DelegateExceptionCallback(new OrtcEmptyFieldException("Application Key is null or empty"));
            }
            else if (String.IsNullOrEmpty(privateKey))
            {
                DelegateExceptionCallback(new OrtcEmptyFieldException("Private Key is null or empty"));
            }
            else if (String.IsNullOrEmpty(channel))
            {
                DelegateExceptionCallback(new OrtcEmptyFieldException("Channel is null or empty"));
            }
            else if (!channel.OrtcIsValidInput())
            {
                DelegateExceptionCallback(new OrtcInvalidCharactersException("Channel has invalid characters"));
            }
            else if (String.IsNullOrEmpty(message))
            {
                DelegateExceptionCallback(new OrtcEmptyFieldException("Message is null or empty"));
            }
            else

            #endregion
            {
                byte[] channelBytes = Encoding.UTF8.GetBytes(channel);

                if (channelBytes.Length > MAX_CHANNEL_SIZE)
                {
                    DelegateExceptionCallback(new OrtcMaxLengthException(String.Format("Channel size exceeds the limit of {0} characters", MAX_CHANNEL_SIZE)));
                }
                else
                {

                    message = message.Replace(Environment.NewLine, "\n");

                    if (channel != String.Empty && message != String.Empty)
                    {
                        try
                        {
                            byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                            ArrayList messageParts = new ArrayList();
                            int pos = 0;
                            int remaining;
                            string messageId = Strings.GenerateId(8);

                            // Multi part
                            while ((remaining = messageBytes.Length - pos) > 0)
                            {
                                byte[] messagePart;

                                if (remaining >= MAX_MESSAGE_SIZE - channelBytes.Length)
                                {
                                    messagePart = new byte[MAX_MESSAGE_SIZE - channelBytes.Length];
                                }
                                else
                                {
                                    messagePart = new byte[remaining];
                                }

                                Array.Copy(messageBytes, pos, messagePart, 0, messagePart.Length);

                                messageParts.Add(Encoding.UTF8.GetString((byte[])messagePart));

                                pos += messagePart.Length;
                            }

                            for (int i = 0; i < messageParts.Count; i++)
                            {
                                string s = String.Format("sendproxy;{0};{1};{2};{3}", applicationKey, privateKey, channel, String.Format("{0}_{1}-{2}_{3}", messageId, i + 1, messageParts.Count, messageParts[i]));

                                DoSend(s);
                            }
                        }
                        catch (Exception ex)
                        {
                            string exName = null;

                            if (ex.InnerException != null)
                            {
                                exName = ex.InnerException.GetType().Name;
                            }

                            switch (exName)
                            {
                                case "OrtcNotConnectedException":
                                    // Server went down
                                    if (IsConnected)
                                    {
                                        DoDisconnect();
                                    }
                                    break;
                                default:
                                    DelegateExceptionCallback(new OrtcGenericException(String.Format("Unable to send: {0}", ex)));
                                    break;
                            }
                        }
                    }
                }

            }
        }

        /// <summary>
        /// Subscribes to a channel.
        /// </summary>
        /// <param name="channel">Channel name.</param>
        /// <param name="subscribeOnReconnected">Subscribe to the specified channel on reconnect.</param>
        /// <param name="onMessage"><see cref="OnMessageDelegate"/> callback.</param>
        /// <example>
        ///   <code>
        /// ortcClient.Subscribe("channelName", true, OnMessageCallback);
        /// private void OnMessageCallback(object sender, string channel, string message)
        /// {
        /// // Do something
        /// }
        ///   </code>
        ///   </example>
        public override void Subscribe(string channel, bool subscribeOnReconnected, OnMessageDelegate onMessage)
        {
            #region Sanity Checks

            bool sanityChecked = true;

            if (!IsConnected)
            {
                DelegateExceptionCallback(new OrtcNotConnectedException("Not connected"));
                sanityChecked = false;
            }
            else if (String.IsNullOrEmpty(channel))
            {
                DelegateExceptionCallback(new OrtcEmptyFieldException("Channel is null or empty"));
                sanityChecked = false;
            }
            else if (!channel.OrtcIsValidInput())
            {
                DelegateExceptionCallback(new OrtcInvalidCharactersException("Channel has invalid characters"));
                sanityChecked = false;
            }
            else if (_subscribedChannels.ContainsKey(channel))
            {
                ChannelSubscription channelSubscription = null;
                _subscribedChannels.TryGetValue(channel, out channelSubscription);

                if (channelSubscription != null)
                {
                    if (channelSubscription.IsSubscribing)
                    {
                        DelegateExceptionCallback(new OrtcSubscribedException(String.Format("Already subscribing to the channel {0}", channel)));
                        sanityChecked = false;
                    }
                    else if (channelSubscription.IsSubscribed)
                    {
                        DelegateExceptionCallback(new OrtcSubscribedException(String.Format("Already subscribed to the channel {0}", channel)));
                        sanityChecked = false;
                    }
                }
            }
            else
            {
                byte[] channelBytes = Encoding.UTF8.GetBytes(channel);

                if (channelBytes.Length > MAX_CHANNEL_SIZE)
                {
                    if (_subscribedChannels.ContainsKey(channel))
                    {
                        ChannelSubscription channelSubscription = null;
                        _subscribedChannels.TryGetValue(channel, out channelSubscription);

                        if (channelSubscription != null)
                        {
                            channelSubscription.IsSubscribing = false;
                        }
                    }

                    DelegateExceptionCallback(new OrtcMaxLengthException(String.Format("Channel size exceeds the limit of {0} characters", MAX_CHANNEL_SIZE)));
                    sanityChecked = false;
                }
            }

            #endregion

            if (sanityChecked)
            {
                var domainChannelCharacterIndex = channel.IndexOf(':');
                var channelToValidate = channel;

                if (domainChannelCharacterIndex > 0)
                {
                    channelToValidate = channel.Substring(0, domainChannelCharacterIndex + 1) + "*";
                }

                string hash = _permissions.Where(c => c.Key == channel || c.Key == channelToValidate).FirstOrDefault().Value;

                if (_permissions != null && _permissions.Count > 0 && String.IsNullOrEmpty(hash))
                {
                    DelegateExceptionCallback(new OrtcNotConnectedException(String.Format("No permission found to subscribe to the channel '{0}'", channel)));
                }
                else
                {
                    if (!_subscribedChannels.ContainsKey(channel))
                    {
                        _subscribedChannels.TryAdd(channel,
                            new ChannelSubscription
                            {
                                IsSubscribing = true,
                                IsSubscribed = false,
                                SubscribeOnReconnected = subscribeOnReconnected,
                                OnMessage = onMessage
                            });
                    }

                    try
                    {
                        if (_subscribedChannels.ContainsKey(channel))
                        {
                            ChannelSubscription channelSubscription = null;
                            _subscribedChannels.TryGetValue(channel, out channelSubscription);

                            channelSubscription.IsSubscribing = true;
                            channelSubscription.IsSubscribed = false;
                            channelSubscription.SubscribeOnReconnected = subscribeOnReconnected;
                            channelSubscription.OnMessage = onMessage;
                        }

                        string s = String.Format("subscribe;{0};{1};{2};{3}", _applicationKey, _authenticationToken, channel, hash);

                        DoSend(s);
                    }
                    catch (Exception ex)
                    {
                        string exName = null;

                        if (ex.InnerException != null)
                        {
                            exName = ex.InnerException.GetType().Name;
                        }

                        switch (exName)
                        {
                            case "OrtcNotConnectedException":
                                // Server went down
                                if (IsConnected)
                                {
                                    DoDisconnect();
                                }
                                break;
                            default:
                                DelegateExceptionCallback(new OrtcGenericException(String.Format("Unable to subscribe: {0}", ex)));
                                break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Unsubscribes from a channel.
        /// </summary>
        /// <param name="channel">Channel name.</param>
        /// <example>
        ///   <code>
        /// ortcClient.Unsubscribe("channelName");
        ///   </code>
        ///   </example>
        public override void Unsubscribe(string channel)
        {
            #region Sanity Checks

            bool sanityChecked = true;

            if (!IsConnected)
            {
                DelegateExceptionCallback(new OrtcNotConnectedException("Not connected"));
                sanityChecked = false;
            }
            else if (String.IsNullOrEmpty(channel))
            {
                DelegateExceptionCallback(new OrtcEmptyFieldException("Channel is null or empty"));
                sanityChecked = false;
            }
            else if (!channel.OrtcIsValidInput())
            {
                DelegateExceptionCallback(new OrtcInvalidCharactersException("Channel has invalid characters"));
                sanityChecked = false;
            }
            else if (!_subscribedChannels.ContainsKey(channel))
            {
                DelegateExceptionCallback(new OrtcNotSubscribedException(String.Format("Not subscribed to the channel {0}", channel)));
                sanityChecked = false;
            }
            else if (_subscribedChannels.ContainsKey(channel))
            {
                ChannelSubscription channelSubscription = null;
                _subscribedChannels.TryGetValue(channel, out channelSubscription);

                if (channelSubscription != null && !channelSubscription.IsSubscribed)
                {
                    DelegateExceptionCallback(new OrtcNotSubscribedException(String.Format("Not subscribed to the channel {0}", channel)));
                    sanityChecked = false;
                }
            }
            else
            {
                byte[] channelBytes = Encoding.UTF8.GetBytes(channel);

                if (channelBytes.Length > MAX_CHANNEL_SIZE)
                {
                    DelegateExceptionCallback(new OrtcMaxLengthException(String.Format("Channel size exceeds the limit of {0} characters", MAX_CHANNEL_SIZE)));
                    sanityChecked = false;
                }
            }

            #endregion

            if (sanityChecked)
            {
                try
                {
                    string s = String.Format("unsubscribe;{0};{1}", _applicationKey, channel);

                    DoSend(s);
                }
                catch (Exception ex)
                {
                    string exName = null;

                    if (ex.InnerException != null)
                    {
                        exName = ex.InnerException.GetType().Name;
                    }

                    switch (exName)
                    {
                        case "OrtcNotConnectedException":
                            // Server went down
                            if (IsConnected)
                            {
                                DoDisconnect();
                            }
                            break;
                        default:
                            DelegateExceptionCallback(new OrtcGenericException(String.Format("Unable to unsubscribe: {0}", ex)));
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Disconnects from the gateway.
        /// </summary>
        /// <example>
        ///   <code>
        /// ortcClient.Disconnect();
        ///   </code>
        ///   </example>
        public override void Disconnect()
        {
            DoStopReconnecting();

            // Clear subscribed channels
            _subscribedChannels.Clear();

            #region Sanity Checks

            if (!IsConnected)
            {
                DelegateExceptionCallback(new OrtcNotConnectedException("Not connected"));
            }
            else

            #endregion
            {
                DoDisconnect();
            }
        }

        /// <summary>
        /// Indicates whether is subscribed to a channel.
        /// </summary>
        /// <param name="channel">The channel name.</param>
        /// <returns>
        ///   <c>true</c> if subscribed to the channel; otherwise, <c>false</c>.
        /// </returns>
        public override bool IsSubscribed(string channel)
        {
            bool result = false;

            #region Sanity Checks

            if (!IsConnected)
            {
                DelegateExceptionCallback(new OrtcNotConnectedException("Not connected"));
            }
            else if (String.IsNullOrEmpty(channel))
            {
                DelegateExceptionCallback(new OrtcEmptyFieldException("Channel is null or empty"));
            }
            else if (!channel.OrtcIsValidInput())
            {
                DelegateExceptionCallback(new OrtcInvalidCharactersException("Channel has invalid characters"));
            }
            else

            #endregion
            {
                result = false;

                if (_subscribedChannels.ContainsKey(channel))
                {
                    ChannelSubscription channelSubscription = null;
                    _subscribedChannels.TryGetValue(channel, out channelSubscription);

                    if (channelSubscription != null && channelSubscription.IsSubscribed)
                    {
                        result = true;
                    }
                }
            }

            return result;
        }

        public override void Presence(String channel, OnPresenceDelegate callback)
        {
            var isCluster = !String.IsNullOrEmpty(this.ClusterUrl);
            var url = String.IsNullOrEmpty(this.ClusterUrl) ? this.Url : this.ClusterUrl;

            Ibt.Ortc.Api.Extensibility.Presence.GetPresence(url, isCluster, this._applicationKey, this._authenticationToken, channel, callback);
        }

        public override void EnablePresence(String privateKey, String channel, bool metadata, OnEnablePresenceDelegate callback)
        {
            var isCluster = !String.IsNullOrEmpty(this.ClusterUrl);
            var url = String.IsNullOrEmpty(this.ClusterUrl) ? this.Url : this.ClusterUrl;

            Ibt.Ortc.Api.Extensibility.Presence.EnablePresence(url, isCluster, this._applicationKey, privateKey, channel, metadata, callback);
        }

        public override void DisablePresence(String privateKey, String channel, OnDisablePresenceDelegate callback)
        {
            var isCluster = !String.IsNullOrEmpty(this.ClusterUrl);
            var url = String.IsNullOrEmpty(this.ClusterUrl) ? this.Url : this.ClusterUrl;

            Ibt.Ortc.Api.Extensibility.Presence.DisablePresence(url, isCluster, this._applicationKey, privateKey, channel, callback);
        }

        #endregion

        #region Private Methods (13)

        /// <summary>
        /// Processes the operation validated.
        /// </summary>
        /// <param name="arguments">The arguments.</param>
        private void ProcessOperationValidated(string arguments)
        {
            if (!String.IsNullOrEmpty(arguments))
            {
                _reconnectStartedAt = null;

                bool isValid = false;

                // Try to match with authentication
                Match validatedAuthMatch = Regex.Match(arguments, VALIDATED_PATTERN);

                if (validatedAuthMatch.Success)
                {
                    isValid = true;

                    string userPermissions = String.Empty;

                    if (validatedAuthMatch.Groups["up"].Length > 0)
                    {
                        userPermissions = validatedAuthMatch.Groups["up"].Value;
                    }

                    if (validatedAuthMatch.Groups["set"].Length > 0)
                    {
                        _sessionExpirationTime = int.Parse(validatedAuthMatch.Groups["set"].Value);
                    }

                    if (String.IsNullOrEmpty(ReadLocalStorage(_applicationKey, _sessionExpirationTime)))
                    {
                        CreateLocalStorage(_applicationKey);
                    }

                    if (!String.IsNullOrEmpty(userPermissions) && userPermissions != "null")
                    {
                        MatchCollection matchCollection = Regex.Matches(userPermissions, PERMISSIONS_PATTERN);

                        var permissions = new List<KeyValuePair<string, string>>();

                        foreach (Match match in matchCollection)
                        {
                            string channel = match.Groups["key"].Value;
                            string hash = match.Groups["value"].Value;

                            permissions.Add(new KeyValuePair<string, string>(channel, hash));
                        }

                        _permissions = new List<KeyValuePair<string, string>>(permissions);
                    }
                }

                if (isValid)
                {
                    _isConnecting = false;
                    IsConnected = true;
                    if (HeartbeatActive)
                    {
                        _heartbeatTimer.Interval = HeartbeatTime * 1000;
                        _heartbeatTimer.Start();
                    }
                    if (_alreadyConnectedFirstTime)
                    {
                        ArrayList channelsToRemove = new ArrayList();

                        // Subscribe to the previously subscribed channels
                        foreach (KeyValuePair<string, ChannelSubscription> item in _subscribedChannels)
                        {
                            string channel = item.Key;
                            ChannelSubscription channelSubscription = item.Value;

                            // Subscribe again
                            if (channelSubscription.SubscribeOnReconnected && (channelSubscription.IsSubscribing || channelSubscription.IsSubscribed))
                            {
                                channelSubscription.IsSubscribing = true;
                                channelSubscription.IsSubscribed = false;

                                var domainChannelCharacterIndex = channel.IndexOf(':');
                                var channelToValidate = channel;

                                if (domainChannelCharacterIndex > 0)
                                {
                                    channelToValidate = channel.Substring(0, domainChannelCharacterIndex + 1) + "*";
                                }

                                string hash = _permissions.Where(c => c.Key == channel || c.Key == channelToValidate).FirstOrDefault().Value;

                                string s = String.Format("subscribe;{0};{1};{2};{3}", _applicationKey, _authenticationToken, channel, hash);

                                DoSend(s);
                            }
                            else
                            {
                                channelsToRemove.Add(channel);
                            }
                        }

                        for (int i = 0; i < channelsToRemove.Count; i++)
                        {
                            ChannelSubscription removeResult = null;
                            _subscribedChannels.TryRemove(channelsToRemove[i].ToString(), out removeResult);
                        }

                        // Clean messages buffer (can have lost message parts in memory)
                        _multiPartMessagesBuffer.Clear();

                        DelegateReconnectedCallback();
                    }
                    else
                    {
                        _alreadyConnectedFirstTime = true;

                        // Clear subscribed channels
                        _subscribedChannels.Clear();

                        DelegateConnectedCallback();
                    }

                    if (arguments.IndexOf("busy") < 0)
                    {
                        if (_reconnectTimer != null)
                        {
                            _reconnectTimer.Stop();
                        }
                    }

                    _callDisconnectedCallback = true;
                }
            }
        }

        /// <summary>
        /// Processes the operation subscribed.
        /// </summary>
        /// <param name="arguments">The arguments.</param>
        private void ProcessOperationSubscribed(string arguments)
        {
            if (!String.IsNullOrEmpty(arguments))
            {
                Match subscribedMatch = Regex.Match(arguments, CHANNEL_PATTERN);

                if (subscribedMatch.Success)
                {
                    string channelSubscribed = String.Empty;

                    if (subscribedMatch.Groups["channel"].Length > 0)
                    {
                        channelSubscribed = subscribedMatch.Groups["channel"].Value;
                    }

                    if (!String.IsNullOrEmpty(channelSubscribed))
                    {
                        ChannelSubscription channelSubscription = null;
                        _subscribedChannels.TryGetValue(channelSubscribed, out channelSubscription);

                        if (channelSubscription != null)
                        {
                            channelSubscription.IsSubscribing = false;
                            channelSubscription.IsSubscribed = true;
                        }

                        DelegateSubscribedCallback(channelSubscribed);
                    }
                }
            }
        }

        /// <summary>
        /// Processes the operation unsubscribed.
        /// </summary>
        /// <param name="arguments">The arguments.</param>
        private void ProcessOperationUnsubscribed(string arguments)
        {
            if (!String.IsNullOrEmpty(arguments))
            {
                Match unsubscribedMatch = Regex.Match(arguments, CHANNEL_PATTERN);

                if (unsubscribedMatch.Success)
                {
                    string channelUnsubscribed = String.Empty;

                    if (unsubscribedMatch.Groups["channel"].Length > 0)
                    {
                        channelUnsubscribed = unsubscribedMatch.Groups["channel"].Value;
                    }

                    if (!String.IsNullOrEmpty(channelUnsubscribed))
                    {
                        ChannelSubscription channelSubscription = null;
                        _subscribedChannels.TryGetValue(channelUnsubscribed, out channelSubscription);

                        if (channelSubscription != null)
                        {
                            channelSubscription.IsSubscribed = false;
                        }

                        DelegateUnsubscribedCallback(channelUnsubscribed);
                    }
                }
            }
        }

        /// <summary>
        /// Processes the operation error.
        /// </summary>
        /// <param name="arguments">The arguments.</param>
        private void ProcessOperationError(string arguments)
        {
            if (!String.IsNullOrEmpty(arguments))
            {
                Match errorMatch = Regex.Match(arguments, EXCEPTION_PATTERN);

                if (errorMatch.Success)
                {
                    string op = String.Empty;
                    string error = String.Empty;
                    string channel = String.Empty;

                    if (errorMatch.Groups["op"].Length > 0)
                    {
                        op = errorMatch.Groups["op"].Value;
                    }

                    if (errorMatch.Groups["error"].Length > 0)
                    {
                        error = errorMatch.Groups["error"].Value;
                    }

                    if (errorMatch.Groups["channel"].Length > 0)
                    {
                        channel = errorMatch.Groups["channel"].Value;
                    }

                    if (!String.IsNullOrEmpty(error))
                    {
                        DelegateExceptionCallback(new OrtcGenericException(error));
                    }

                    if (!String.IsNullOrEmpty(op))
                    {
                        switch (op)
                        {
                            case "validate":
                                if (!String.IsNullOrEmpty(error) && (error.Contains("Unable to connect") || error.Contains("Server is too busy")))
                                {
                                    IsConnected = false;
                                    DoReconnect();
                                }
                                else
                                {
                                    DoStopReconnecting();
                                }
                                break;
                            case "subscribe":
                                if (!String.IsNullOrEmpty(channel))
                                {
                                    ChannelSubscription channelSubscription = null;
                                    _subscribedChannels.TryGetValue(channel, out channelSubscription);

                                    if (channelSubscription != null)
                                    {
                                        channelSubscription.IsSubscribing = false;
                                    }
                                }
                                break;
                            case "subscribe_maxsize":
                            case "unsubscribe_maxsize":
                            case "send_maxsize":
                                if (!String.IsNullOrEmpty(channel))
                                {
                                    ChannelSubscription channelSubscription = null;
                                    _subscribedChannels.TryGetValue(channel, out channelSubscription);

                                    if (channelSubscription != null)
                                    {
                                        channelSubscription.IsSubscribing = false;
                                    }
                                }

                                DoStopReconnecting();
                                DoDisconnect();
                                break;
                            default:
                                break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Processes the operation received.
        /// </summary>
        /// <param name="message">The message.</param>
        //private void OldProcessOperationReceived(string message)
        //{
        //    Match receivedMatch = Regex.Match(message, RECEIVED_PATTERN);

        //    // Received
        //    if (receivedMatch.Success)
        //    {
        //        string channelReceived = String.Empty;
        //        string messageReceived = String.Empty;

        //        if (receivedMatch.Groups["channel"].Length > 0)
        //        {
        //            channelReceived = receivedMatch.Groups["channel"].Value;
        //        }

        //        if (receivedMatch.Groups["message"].Length > 0)
        //        {
        //            messageReceived = receivedMatch.Groups["message"].Value;
        //        }

        //        if (!String.IsNullOrEmpty(channelReceived) && !String.IsNullOrEmpty(messageReceived) && _subscribedChannels.ContainsKey(channelReceived))
        //        {
        //            messageReceived = messageReceived.Replace(@"\\n", Environment.NewLine).Replace("\\\\\"", @"""").Replace("\\\\\\\\", @"\");

        //            // Multi part
        //            Match multiPartMatch = Regex.Match(messageReceived, MULTI_PART_MESSAGE_PATTERN);

        //            string messageId = String.Empty;
        //            int messageCurrentPart = 1;
        //            int messageTotalPart = 1;
        //            bool lastPart = false;
        //            List<BufferedMessage> messageParts = null;

        //            if (multiPartMatch.Success)
        //            {
        //                if (multiPartMatch.Groups["messageId"].Length > 0)
        //                {
        //                    messageId = multiPartMatch.Groups["messageId"].Value;
        //                }

        //                if (multiPartMatch.Groups["messageCurrentPart"].Length > 0)
        //                {
        //                    messageCurrentPart = Int32.Parse(multiPartMatch.Groups["messageCurrentPart"].Value);
        //                }

        //                if (multiPartMatch.Groups["messageTotalPart"].Length > 0)
        //                {
        //                    messageTotalPart = Int32.Parse(multiPartMatch.Groups["messageTotalPart"].Value);
        //                }

        //                if (multiPartMatch.Groups["message"].Length > 0)
        //                {
        //                    messageReceived = multiPartMatch.Groups["message"].Value;
        //                }
        //            }

        //            // Is a message part
        //            if (!String.IsNullOrEmpty(messageId))
        //            {
        //                if (!_multiPartMessagesBuffer.ContainsKey(messageId))
        //                {
        //                    _multiPartMessagesBuffer.TryAdd(messageId, new List<BufferedMessage>());
        //                }

        //                _multiPartMessagesBuffer.TryGetValue(messageId, out messageParts);

        //                if (messageParts != null)
        //                {
        //                    messageParts.Add(new BufferedMessage(messageCurrentPart, messageReceived));

        //                    // Last message part
        //                    if (messageParts.Count == messageTotalPart)
        //                    {
        //                        messageParts.Sort();

        //                        lastPart = true;
        //                    }
        //                }
        //            }
        //            // Message does not have multipart, like the messages received at announcement channels
        //            else
        //            {
        //                lastPart = true;
        //            }

        //            if (lastPart)
        //            {
        //                if (_subscribedChannels.ContainsKey(channelReceived))
        //                {
        //                    ChannelSubscription channelSubscription = null;
        //                    _subscribedChannels.TryGetValue(channelReceived, out channelSubscription);

        //                    if (channelSubscription != null)
        //                    {
        //                        var ev = channelSubscription.OnMessage;

        //                        if (ev != null)
        //                        {
        //                            if (!String.IsNullOrEmpty(messageId) && _multiPartMessagesBuffer.ContainsKey(messageId))
        //                            {
        //                                messageReceived = String.Empty;
        //                                lock (messageParts)
        //                                {
        //                                    foreach (BufferedMessage part in messageParts)
        //                                    {
        //                                        if (part != null)
        //                                        {
        //                                            messageReceived = String.Format("{0}{1}", messageReceived, part.Message);
        //                                        }
        //                                    }
        //                                }

        //                                // Remove from messages buffer
        //                                List<BufferedMessage> removeResult = null;
        //                                _multiPartMessagesBuffer.TryRemove(messageId, out removeResult);
        //                            }

        //                            if (!String.IsNullOrEmpty(messageReceived))
        //                            {
        //                                if (_synchContext != null)
        //                                {
        //                                    _synchContext.Post(obj => ev(obj, channelReceived, messageReceived), this);
        //                                }
        //                                else
        //                                {
        //                                    ev(this, channelReceived, messageReceived);
        //                                }
        //                            }
        //                        }
        //                    }
        //                }
        //            }
        //        }
        //    }
        //    else
        //    {
        //        // Unknown
        //        DelegateExceptionCallback(new OrtcGenericException(String.Format("Unknown message received: {0}", message)));

        //        //DoDisconnect();
        //    }
        //}

        private void ProcessOperationReceived(string message)
        {
            Match receivedMatch = Regex.Match(message, RECEIVED_PATTERN);

            // Received
            if (receivedMatch.Success)
            {
                string channelReceived = String.Empty;
                string messageReceived = String.Empty;

                if (receivedMatch.Groups["channel"].Length > 0)
                {
                    channelReceived = receivedMatch.Groups["channel"].Value;
                }

                if (receivedMatch.Groups["message"].Length > 0)
                {
                    messageReceived = receivedMatch.Groups["message"].Value;
                }

                if (!String.IsNullOrEmpty(channelReceived) && !String.IsNullOrEmpty(messageReceived) && _subscribedChannels.ContainsKey(channelReceived))
                {
                    messageReceived = messageReceived.Replace(@"\\n", Environment.NewLine).Replace("\\\\\"", @"""").Replace("\\\\\\\\", @"\");

                    // Multi part
                    Match multiPartMatch = Regex.Match(messageReceived, MULTI_PART_MESSAGE_PATTERN);

                    string messageId = String.Empty;
                    int messageCurrentPart = 1;
                    int messageTotalPart = 1;
                    bool lastPart = false;
                    ConcurrentDictionary<int, BufferedMessage> messageParts = null;

                    if (multiPartMatch.Success)
                    {
                        if (multiPartMatch.Groups["messageId"].Length > 0)
                        {
                            messageId = multiPartMatch.Groups["messageId"].Value;
                        }

                        if (multiPartMatch.Groups["messageCurrentPart"].Length > 0)
                        {
                            messageCurrentPart = Int32.Parse(multiPartMatch.Groups["messageCurrentPart"].Value);
                        }

                        if (multiPartMatch.Groups["messageTotalPart"].Length > 0)
                        {
                            messageTotalPart = Int32.Parse(multiPartMatch.Groups["messageTotalPart"].Value);
                        }

                        if (multiPartMatch.Groups["message"].Length > 0)
                        {
                            messageReceived = multiPartMatch.Groups["message"].Value;
                        }
                    }

                    lock (_multiPartMessagesBuffer)
                    {
                        // Is a message part
                        if (!String.IsNullOrEmpty(messageId))
                        {
                            if (!_multiPartMessagesBuffer.ContainsKey(messageId))
                            {
                                _multiPartMessagesBuffer.TryAdd(messageId, new ConcurrentDictionary<int, BufferedMessage>());
                            }


                            _multiPartMessagesBuffer.TryGetValue(messageId, out messageParts);

                            if (messageParts != null)
                            {
                                lock (messageParts)
                                {

                                    messageParts.TryAdd(messageCurrentPart, new BufferedMessage(messageCurrentPart, messageReceived));

                                    // Last message part
                                    if (messageParts.Count == messageTotalPart)
                                    {
                                        //messageParts.Sort();

                                        lastPart = true;
                                    }
                                }
                            }
                        }
                        // Message does not have multipart, like the messages received at announcement channels
                        else
                        {
                            lastPart = true;
                        }

                        if (lastPart)
                        {
                            if (_subscribedChannels.ContainsKey(channelReceived))
                            {
                                ChannelSubscription channelSubscription = null;
                                _subscribedChannels.TryGetValue(channelReceived, out channelSubscription);

                                if (channelSubscription != null)
                                {
                                    var ev = channelSubscription.OnMessage;

                                    if (ev != null)
                                    {
                                        if (!String.IsNullOrEmpty(messageId) && _multiPartMessagesBuffer.ContainsKey(messageId))
                                        {
                                            messageReceived = String.Empty;
                                            //lock (messageParts)
                                            //{
                                            var bufferedMultiPartMessages = new List<BufferedMessage>();

                                            foreach (var part in messageParts.Keys)
                                            {
                                                bufferedMultiPartMessages.Add(messageParts[part]);
                                            }

                                            bufferedMultiPartMessages.Sort();

                                            foreach (var part in bufferedMultiPartMessages)
                                            {
                                                if (part != null)
                                                {
                                                    messageReceived = String.Format("{0}{1}", messageReceived, part.Message);
                                                }
                                            }
                                            //}

                                            // Remove from messages buffer
                                            ConcurrentDictionary<int, BufferedMessage> removeResult = null;
                                            _multiPartMessagesBuffer.TryRemove(messageId, out removeResult);
                                        }

                                        if (!String.IsNullOrEmpty(messageReceived))
                                        {
                                            if (_synchContext != null)
                                            {
                                                _synchContext.Post(obj => ev(obj, channelReceived, messageReceived), this);
                                            }
                                            else
                                            {
                                                ev(this, channelReceived, messageReceived);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                // Unknown
                DelegateExceptionCallback(new OrtcGenericException(String.Format("Unknown message received: {0}", message)));

                //DoDisconnect();
            }
        }


        /// <summary>
        /// Do the Connect Task
        /// </summary>
        private void DoConnect()
        {
            _isConnecting = true;
            _callDisconnectedCallback = false;

            if (IsCluster)
            {
                try
                {
                    Url = GetUrlFromCluster();

                    IsCluster = true;

                    if (String.IsNullOrEmpty(Url))
                    {
                        DelegateExceptionCallback(new OrtcEmptyFieldException("Unable to get URL from cluster"));
                        DoReconnect();
                    }
                }
                catch (Exception ex)
                {
                    if (!_stopReconnecting)
                    {
                        DelegateExceptionCallback(new OrtcNotConnectedException(ex.Message));
                        DoReconnect();
                    }
                }
            }

            if (!String.IsNullOrEmpty(Url))
            {
                try
                {
                    _webSocketConnection.Connect(Url);

                    // Just in case the server does not respond
                    //
                    _waitingServerResponse = true;

                    StartReconnectTimer();
                    //
                }
                catch (OrtcEmptyFieldException ex)
                {
                    DelegateExceptionCallback(new OrtcNotConnectedException(ex.Message));
                    DoStopReconnecting();
                }
                catch (Exception ex)
                {
                    DelegateExceptionCallback(new OrtcNotConnectedException(ex.Message));
                    _isConnecting = false;
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        private void DoReconnect()
        {
            if (!_stopReconnecting && !IsConnected)
            {
                if (_reconnectStartedAt != null)
                {
                    StartReconnectTimer();
                }
                else
                {
                    _reconnectStartedAt = DateTime.Now;

                    DelegateReconnectingCallback();

                    DoConnect();
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        private void DoStopReconnecting()
        {
            _isConnecting = false;
            _alreadyConnectedFirstTime = false;

            // Stop the connecting/reconnecting process
            _stopReconnecting = true;

            _reconnectStartedAt = null;

            if (_reconnectTimer != null)
            {
                _reconnectTimer.Stop();
            }
        }

        /// <summary>
        /// Disconnect the TCP client.
        /// </summary>
        private void DoDisconnect()
        {
            _reconnectStartedAt = null;
            _heartbeatTimer.Enabled = false;
            try
            {
                _webSocketConnection.Close();
            }
            catch (Exception ex)
            {
                DelegateExceptionCallback(new OrtcGenericException(String.Format("Error disconnecting: {0}", ex)));
            }
        }

        /// <summary>
        /// Sends a message through the TCP client.
        /// </summary>
        /// <param name="str">The message to be sent.</param>
        private void DoSend(string message)
        {
            try
            {
                _webSocketConnection.Send(message);
            }
            catch (Exception ex)
            {
                DelegateExceptionCallback(new OrtcGenericException(String.Format("Unable to send: {0}", ex)));
            }
        }

        private void StartReconnectTimer()
        {
            if (_reconnectTimer != null)
            {
                _reconnectTimer.Stop();
            }

            _reconnectTimer = new System.Timers.Timer();

            _reconnectTimer.AutoReset = false;
            _reconnectTimer.Elapsed += new ElapsedEventHandler(_reconnectTimer_Elapsed);
            _reconnectTimer.Interval = ConnectionTimeout;
            _reconnectTimer.Start();
        }

        /// <summary>
        /// Gets the URL from cluster.
        /// </summary>
        /// <returns></returns>
        private string GetUrlFromCluster()
        {
            string newUrl = String.Empty;

            if (!String.IsNullOrEmpty(ClusterUrl))
            {
                try
                {
                    var clusterRequestParameter = !String.IsNullOrEmpty(_applicationKey) ? String.Format("appkey={0}", _applicationKey) : String.Empty;

                    var tempClusterUrl = String.Format("{0}{1}?{2}", ClusterUrl, ClusterUrl[ClusterUrl.Length - 1] != '/' ? "/" : String.Empty, clusterRequestParameter);

                    var request = (HttpWebRequest)WebRequest.Create(tempClusterUrl);

                    request.Proxy = null;
                    request.Timeout = 10000;
                    request.ProtocolVersion = HttpVersion.Version11;
                    request.Method = "GET";

                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls;

                    using (WebResponse response = request.GetResponse())
                    {
                        using (Stream stream = response.GetResponseStream())
                        {
                            if (stream != null)
                            {
                                using (var reader = new StreamReader(stream))
                                {
                                    string responseText = reader.ReadToEnd();
                                    Match match = Regex.Match(responseText, CLUSTER_RESPONSE_PATTERN);
                                    newUrl = match.Groups["host"].Value;
                                }
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    throw;
                }
            }

            return newUrl;
        }

        #endregion

        #region Events (7)

        /// <summary>
        /// Occurs when a connection attempt was successful.
        /// </summary>
        public override event OnConnectedDelegate OnConnected;

        /// <summary>
        /// Occurs when the client connection terminated. 
        /// </summary>
        public override event OnDisconnectedDelegate OnDisconnected;

        /// <summary>
        /// Occurs when the client subscribed to a channel.
        /// </summary>
        public override event OnSubscribedDelegate OnSubscribed;

        /// <summary>
        /// Occurs when the client unsubscribed from a channel.
        /// </summary>
        public override event OnUnsubscribedDelegate OnUnsubscribed;

        /// <summary>
        /// Occurs when there is an error.
        /// </summary>
        public override event OnExceptionDelegate OnException;

        /// <summary>
        /// Occurs when a client attempts to reconnect.
        /// </summary>
        public override event OnReconnectingDelegate OnReconnecting;

        /// <summary>
        /// Occurs when a client reconnected.
        /// </summary>
        public override event OnReconnectedDelegate OnReconnected;
        

        #endregion

        #region Events handlers (6)

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            e.SetObserved();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void _reconnectTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            if (!_stopReconnecting && !IsConnected)
            {
                if (_waitingServerResponse)
                {
                    _waitingServerResponse = false;
                    DelegateExceptionCallback(new OrtcNotConnectedException("Unable to connect"));
                }

                _reconnectStartedAt = DateTime.Now;

                DelegateReconnectingCallback();

                DoConnect();
            }
        }

        private void _heartbeatTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            if (IsConnected)
            {
                DoSend("b");
            }
        }

        /// <summary>
        /// 
        /// </summary>
        private void _webSocketConnection_OnOpened()
        {
            // Do nothing
        }

        /// <summary>
        /// 
        /// </summary>
        private void _webSocketConnection_OnClosed()
        {
            // Clear user permissions
            _permissions.Clear();

            _isConnecting = false;
            IsConnected = false;
            _heartbeatTimer.Enabled = false;

            if (_callDisconnectedCallback)
            {
                DelegateDisconnectedCallback();
                DoReconnect();
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="error"></param>
        private void _webSocketConnection_OnError(Exception error)
        {
            if (!_stopReconnecting)
            {
                if (_isConnecting)
                {
                    DelegateExceptionCallback(new OrtcGenericException(error.Message));

                    DoReconnect();
                }
                else
                {
                    DelegateExceptionCallback(new OrtcGenericException(String.Format("WebSocketConnection exception: {0}", error)));
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="message"></param>
        private void _webSocketConnection_OnMessageReceived(string message)
        {
            if (!String.IsNullOrEmpty(message))
            {
                // Open
                if (message == "o")
                {
                    try
                    {
                        if (String.IsNullOrEmpty(ReadLocalStorage(_applicationKey, _sessionExpirationTime)))
                        {
                            SessionId = Strings.GenerateId(16);
                        }

                        string s;
                        if (HeartbeatActive)
                        {
                            s = String.Format("validate;{0};{1};{2};{3};{4};{5};{6}", _applicationKey, _authenticationToken, AnnouncementSubChannel, SessionId,
                                ConnectionMetadata, HeartbeatTime, HeartbeatFails);
                        }
                        else
                        {
                            s = String.Format("validate;{0};{1};{2};{3};{4}", _applicationKey, _authenticationToken, AnnouncementSubChannel, SessionId, ConnectionMetadata);
                        }
                        DoSend(s);
                    }
                    catch (Exception ex)
                    {
                        DelegateExceptionCallback(new OrtcGenericException(String.Format("Exception sending validate: {0}", ex)));
                    }
                }
                // Heartbeat
                else if (message == "h")
                {
                    // Do nothing
                }
                else
                {
                    message = message.Replace("\\\"", @"""");

                    // Update last keep alive time
                    _lastKeepAlive = DateTime.Now;

                    // Operation
                    Match operationMatch = Regex.Match(message, OPERATION_PATTERN);

                    if (operationMatch.Success)
                    {
                        string operation = operationMatch.Groups["op"].Value;
                        string arguments = operationMatch.Groups["args"].Value;

                        switch (operation)
                        {
                            case "ortc-validated":
                                ProcessOperationValidated(arguments);
                                break;
                            case "ortc-subscribed":
                                ProcessOperationSubscribed(arguments);
                                break;
                            case "ortc-unsubscribed":
                                ProcessOperationUnsubscribed(arguments);
                                break;
                            case "ortc-error":
                                ProcessOperationError(arguments);
                                break;
                            default:
                                // Unknown operation
                                DelegateExceptionCallback(new OrtcGenericException(String.Format("Unknown operation \"{0}\" for the message \"{1}\"", operation, message)));

                                DoDisconnect();
                                break;
                        }
                    }
                    else
                    {
                        // Close
                        Match closeOperationMatch = Regex.Match(message, CLOSE_PATTERN);

                        if (!closeOperationMatch.Success)
                        {
                            ProcessOperationReceived(message);
                        }
                    }
                }
            }
        }

        #endregion

        #region Events calls (7)

        private void DelegateConnectedCallback()
        {
            var ev = OnConnected;

            if (ev != null)
            {
                if (_synchContext != null)
                {
                    _synchContext.Post(obj => ev(obj), this);
                }
                else
                {
                    Task.Factory.StartNew(() => ev(this));
                }
            }
        }

        private void DelegateDisconnectedCallback()
        {
            var ev = OnDisconnected;

            if (ev != null)
            {
                if (_synchContext != null)
                {
                    _synchContext.Post(obj => ev(obj), this);
                }
                else
                {
                    Task.Factory.StartNew(() => ev(this));
                }
            }
        }

        private void DelegateSubscribedCallback(string channel)
        {
            var ev = OnSubscribed;

            if (ev != null)
            {
                if (_synchContext != null)
                {
                    _synchContext.Post(obj => ev(obj, channel), this);
                }
                else
                {
                    Task.Factory.StartNew(() => ev(this, channel));
                }
            }
        }

        private void DelegateUnsubscribedCallback(string channel)
        {
            var ev = OnUnsubscribed;

            if (ev != null)
            {
                if (_synchContext != null)
                {
                    _synchContext.Post(obj => ev(obj, channel), this);
                }
                else
                {
                    Task.Factory.StartNew(() => ev(this, channel));
                }
            }
        }

        private void DelegateExceptionCallback(Exception ex)
        {
            var ev = OnException;

            if (ev != null)
            {
                if (_synchContext != null)
                {
                    _synchContext.Post(obj => ev(obj, ex), this);
                }
                else
                {
                    Task.Factory.StartNew(() => ev(this, ex));
                }
            }
        }

        private void DelegateReconnectingCallback()
        {
            var ev = OnReconnecting;

            if (ev != null)
            {
                if (_synchContext != null)
                {
                    _synchContext.Post(obj => ev(obj), this);
                }
                else
                {
                    Task.Factory.StartNew(() => ev(this));
                }
            }
        }

        private void DelegateReconnectedCallback()
        {
            var ev = OnReconnected;

            if (ev != null)
            {
                if (_synchContext != null)
                {
                    _synchContext.Post(obj => ev(obj), this);
                }
                else
                {
                    Task.Factory.StartNew(() => ev(this));
                }
            }
        }

        #endregion
    }
}
