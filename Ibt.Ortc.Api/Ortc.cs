using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Ibt.Ortc.Api.Extensibility;
using System.Web;

namespace Ibt.Ortc.Api
{
    /// <summary>
    /// The channel permission.
    /// </summary>
    public enum ChannelPermissions
    {
        /// <summary>
        /// Read permission
        /// </summary>
        Read = 'r',

        /// <summary>
        /// Read and Write permission
        /// </summary>
        Write = 'w',

        /// <summary>
        /// Presence permission
        /// </summary>
        Presence = 'p'
    }

    /// <summary>
    /// ORTC server side API that contains ORTC factories as plugins.
    /// </summary>
    public class Ortc
    {
        #region Properties (1)

        /// <summary>
        /// Gets or sets factories loaded via MEF, using lazy load method. The object is only instanciated when you will need to use it.
        /// This should not be used.
        /// </summary>
        /// <value>
        /// Ortc factories loaded via MEF.
        /// </value>
        [ImportMany(typeof(IOrtcFactory), AllowRecomposition = true)]
        internal Lazy<IOrtcFactory, IOrtcFactoryAttributes>[] OrtcProviders { get; set; }

        #endregion

        #region Constructors (2)

        /// <summary>
        /// Initializes a new instance of the <see cref="Ortc"/> class and loads all the plugins in the specified directory.
        /// </summary>
        /// <param name="pluginsDirectory">Directory where are the plugins assemblies.</param>
        /// <example>
        /// <code>
        ///     var api = new Api.Ortc("Plugins");
        ///     
        ///     IOrtcFactory factory = api.LoadOrtcFactory("IbtRealTimeSJ");
        /// 
        ///     OrtcClient ortcClient = factory.CreateClient();
        ///     
        ///     // Do something with the created client object (ortcClient)
        /// </code>
        /// </example>        
        [Obsolete("This constructor is deprecated, please use Api.Ortc() instead.")]
        public Ortc(string pluginsDirectory) : this()
        {
            /*
            if (String.IsNullOrEmpty(pluginsDirectory))
            {
                throw new InvalidPluginDirectoryException("Plugins directory can not be null or empty.");
            }

            if (!Directory.Exists(pluginsDirectory))
            {
                throw new InvalidPluginDirectoryException("Plugins directory not found.");
            }

            var catalog = new DirectoryCatalog(pluginsDirectory, "*.dll");

            var container = new CompositionContainer(catalog);

            container.ComposeParts(this);
             */ 
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Ortc"/> class and loads all the plugins in the current directory.
        /// </summary>
        /// <example>
        /// <code>
        ///     var api = new Api.Ortc();
        /// 
        ///     IOrtcFactory factory = api.LoadOrtcFactory("IbtRealTimeSJ");
        /// 
        ///     OrtcClient ortcClient = factory.CreateClient();
        ///     
        ///     // Do something with the created client object (ortcClient)
        /// </code>
        /// </example>
        public Ortc()
        {
            var catalog = new DirectoryCatalog(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "*.dll");

            var container = new CompositionContainer(catalog);

            container.ComposeParts(this);
        }

        #endregion

        #region Public Methods (3)

        /// <summary>
        /// Loads the ORTC factory with the specified ORTC type.
        /// </summary>
        /// <param name="ortcType">The type of the ORTC client created by the factory.</param>
        /// <returns>Loaded instance of ORTC factory of the specified ORTC type.</returns>
        /// <example>
        /// <code>
        ///     var api = new Api.Ortc("Plugins");
        /// 
        ///     IOrtcFactory factory = api.LoadOrtcFactory("IbtRealTimeSJ");
        ///     
        ///     // Use the factory instance to create new clients
        /// </code>
        /// </example>
        public IOrtcFactory LoadOrtcFactory(string ortcType)
        {
            IOrtcFactory iOFactory = new Ibt.Ortc.Plugin.IbtRealTimeSJ.IbtRealTimeSJFactory();
            return iOFactory;
            //Lazy<IOrtcFactory, IOrtcFactoryAttributes> factory = OrtcProviders.Where(f => f.Metadata.FactoryType.Equals(ortcType, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();

            //return factory == null ? null : factory.Value;
        }

        /// <summary>
        /// Saves the authentication token channels permissions in the ORTC server.
        /// </summary>
        /// <param name="url">ORTC server URL.</param>
        /// <param name="isCluster">Indicates whether the ORTC server is in a cluster.</param>
        /// <param name="authenticationToken">Authentication Token which is generated by the application server, for instance a unique session ID.</param>
        /// <param name="authenticationTokenIsPrivate">Indicates whether the authentication token is private (1) or not (0).</param>
        /// <param name="applicationKey">Application Key that was provided to you together with the ORTC service purchasing.</param>
        /// <param name="timeToLive">The authentication token time to live, in other words, the allowed activity time (in seconds).</param>
        /// <param name="privateKey">The private key provided to you together with the ORTC service purchasing.</param>
        /// <param name="permissions">The channels and their permissions (w: write/read or r: read, case sensitive).</param>
        /// <returns>True if the authentication was successful or false if it was not.</returns>
        /// <exception cref="OrtcEmptyFieldException">Server URL can not be null or empty.</exception>
        /// <exception cref="OrtcAuthenticationNotAuthorizedException">Unauthorized by the server.</exception>
        /// <exception cref="OrtcNotConnectedException">Unable to connect to the authentication server.</exception>
        /// <example>
        /// <code>
        ///    // Permissions
        ///    Dictionary&lt;string, ChannelPermissions&gt; permissions = new Dictionary&lt;string, ChannelPermissions&gt;();
        /// 
        ///    permissions.Add("channel1", ChannelPermissions.Read);
        ///    permissions.Add("channel2", ChannelPermissions.Write);
        /// 
        ///    string url = "http://ortc-developers.realtime.co/server/2.1"; 
        ///    bool isCluster = true;
        ///    string authenticationToken = "myAuthenticationToken";
        ///    bool authenticationTokenIsPrivate = true;
        ///    string applicationKey = "myApplicationKey";
        ///    int timeToLive = 1800; // 30 minutes
        ///    string privateKey = "myPrivateKey";
        /// 
        ///    bool authSaved = Ibt.Ortc.Api.Ortc.SaveAuthentication(url, isCluster, authenticationToken, authenticationTokenIsPrivate, applicationKey, timeToLive, privateKey, permissions)) 
        /// </code>
        /// </example>
        public static bool SaveAuthentication(string url, bool isCluster, string authenticationToken, bool authenticationTokenIsPrivate,
                                              string applicationKey, int timeToLive, string privateKey, Dictionary<string, ChannelPermissions> permissions)
        {
            var result = false;

            if (permissions != null && permissions.Count > 0)
            {
                var multiPermissions = new Dictionary<string, List<ChannelPermissions>>();

                foreach (var permission in permissions)
                {
                    var permissionList = new List<ChannelPermissions>();
                    permissionList.Add(permission.Value);

                    multiPermissions.Add(permission.Key, permissionList);
                }

                result = Ortc.SaveAuthentication(url, isCluster, authenticationToken, authenticationTokenIsPrivate, applicationKey, timeToLive, privateKey, multiPermissions);
            }

            return result;
        }

        /// <summary>
        /// Saves the authentication token channels permissions in the ORTC server.
        /// </summary>
        /// <param name="url">ORTC server URL.</param>
        /// <param name="isCluster">Indicates whether the ORTC server is in a cluster.</param>
        /// <param name="authenticationToken">Authentication Token which is generated by the application server, for instance a unique session ID.</param>
        /// <param name="authenticationTokenIsPrivate">Indicates whether the authentication token is private (1) or not (0).</param>
        /// <param name="applicationKey">Application Key that was provided to you together with the ORTC service purchasing.</param>
        /// <param name="timeToLive">The authentication token time to live, in other words, the allowed activity time (in seconds).</param>
        /// <param name="privateKey">The private key provided to you together with the ORTC service purchasing.</param>
        /// <param name="permissions">The channels and their permissions (w: write/read or r: read, case sensitive).</param>
        /// <returns>True if the authentication was successful or false if it was not.</returns>
        /// <exception cref="OrtcEmptyFieldException">Server URL can not be null or empty.</exception>
        /// <exception cref="OrtcAuthenticationNotAuthorizedException">Unauthorized by the server.</exception>
        /// <exception cref="OrtcNotConnectedException">Unable to connect to the authentication server.</exception>
        /// <example>
        /// <code>
        ///    // Permissions
        ///    Dictionary&lt;string, ChannelPermissions&gt; permissions = new Dictionary&lt;string, List&lt;ChannelPermissions&gt;&gt;();
        /// 
        ///    Dictionary&lt;string, List&lt;ChannelPermissions&gt;&gt; channelPermissions = new Dictionary&lt;string, List&lt;ChannelPermissions&gt;&gt;();
        ///    var permissionsList = new List&lt;ChannelPermissions&gt;();
        /// 
        ///    permissionsList.Add(ChannelPermissions.Write);
        ///    permissionsList.Add(ChannelPermissions.Presence);
        ///    
        ///    channelPermissions.Add("channel1", permissionsList);
        /// 
        ///    string url = "http://ortc-developers.realtime.co/server/2.1"; 
        ///    bool isCluster = true;
        ///    string authenticationToken = "myAuthenticationToken";
        ///    bool authenticationTokenIsPrivate = true;
        ///    string applicationKey = "myApplicationKey";
        ///    int timeToLive = 1800; // 30 minutes
        ///    string privateKey = "myPrivateKey";
        /// 
        ///    bool authSaved = Ibt.Ortc.Api.Ortc.SaveAuthentication(url, isCluster, authenticationToken, authenticationTokenIsPrivate, applicationKey, timeToLive, privateKey, channelPermissions)) 
        /// </code>
        /// </example>
        public static bool SaveAuthentication(string url, bool isCluster, string authenticationToken, bool authenticationTokenIsPrivate,
                                              string applicationKey, int timeToLive, string privateKey, Dictionary<string, List<ChannelPermissions>> permissions)
        {
            #region Sanity Checks

            if (String.IsNullOrEmpty(url))
            {
                throw new OrtcEmptyFieldException("URL is null or empty.");
            }
            else if (String.IsNullOrEmpty(applicationKey))
            {
                throw new OrtcEmptyFieldException("Application Key is null or empty.");
            }
            else if (String.IsNullOrEmpty(authenticationToken))
            {
                throw new OrtcEmptyFieldException("Authentication Token is null or empty.");
            }
            else if (String.IsNullOrEmpty(privateKey))
            {
                throw new OrtcEmptyFieldException("Private Key is null or empty.");
            }

            #endregion

            string connectionUrl = url;

            if (isCluster)
            {
                connectionUrl = DoGetClusterLogic(url,applicationKey);
            }

            bool ret = false;

            ServicePointManager.ServerCertificateValidationCallback += ValidateRemoteCertificate;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls;

            connectionUrl = connectionUrl.Last() == '/' ? connectionUrl : connectionUrl + "/";
            var request = (HttpWebRequest)WebRequest.Create(String.Format("{0}authenticate", connectionUrl));

            request.Proxy = null;
            request.KeepAlive = false;
            request.Timeout = 15000;
            request.ProtocolVersion = HttpVersion.Version11;
            request.Method = "POST";

            string postParameters = String.Format("AT={0}&PVT={1}&AK={2}&TTL={3}&PK={4}", authenticationToken, authenticationTokenIsPrivate ? 1 : 0, applicationKey, timeToLive, privateKey);

            if (permissions != null && permissions.Count > 0)
            {
                postParameters += String.Format("&TP={0}", permissions.Count);
                foreach (var permission in permissions)
                {
                    var permissionItemText = String.Format("{0}=",permission.Key);
                    foreach(var permissionItemValue in permission.Value.ToList())
                    {
                        permissionItemText += ((char)permissionItemValue).ToString();
                    }

                    postParameters += String.Format("&{0}", permissionItemText);
                }
            }

            byte[] postBytes = Encoding.UTF8.GetBytes(postParameters);

            request.ContentType = "application/x-www-form-urlencoded";
            request.ContentLength = postBytes.Length;

            HttpWebResponse response = null;

            try
            {
                using (Stream requestStream = request.GetRequestStream())
                {
                    requestStream.Write(postBytes, 0, postBytes.Length);
                    requestStream.Close();
                }

                response = (HttpWebResponse)request.GetResponse();
            }
            catch (WebException ex)
            {
                using (WebResponse errorResponse = ex.Response)
                {
                    var httpResponse = (HttpWebResponse)errorResponse;

                    if (httpResponse != null && httpResponse.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        Stream errorResponseStream = httpResponse.GetResponseStream();
                        if (errorResponseStream != null)
                        {
                            using (var streamReader = new StreamReader(errorResponseStream))
                            {
                                string error = streamReader.ReadToEnd();

                                throw new OrtcAuthenticationNotAuthorizedException(error);
                            }
                        }
                    }
                }

                throw new OrtcNotConnectedException(String.Format("Unable to connect to the authentication server {0}.", connectionUrl));
            }

            if (response != null)
            {
                ret = response.StatusCode == HttpStatusCode.Created;

                response.Close();
            }

            return ret;
        }

        /// <summary>
        /// Sends a message to a channel.
        /// </summary>
        /// <param name="url">ORTC server URL.</param>
        /// <param name="isCluster">Indicates whether the ORTC server is in a cluster.</param>
        /// <param name="authenticationToken">Authentication Token which is generated by the application server, for instance a unique session ID.</param>
        /// <param name="applicationKey">Application Key that was provided to you together with the ORTC service purchasing.</param>
        /// <param name="privateKey">The private key provided to you together with the ORTC service purchasing.</param>
        /// <param name="channel">The channel where the message will be sent.</param>
        /// <param name="message">The message to send.</param>
        /// <returns>True if the send was successful or false if it was not.</returns>
        /// <exception cref="OrtcEmptyFieldException">Server URL can not be null or empty.</exception>
        /// <exception cref="OrtcAuthenticationNotAuthorizedException">Unauthorized by the server.</exception>
        /// <exception cref="OrtcNotConnectedException">Unable to connect to the authentication server.</exception>
        /// <example>
        /// <code>
        ///    string url = "http://ortc_server"; // ORTC HTTP server
        ///    bool isCluster = false;
        ///    string authenticationToken = "myAuthenticationToken";
        ///    string applicationKey = "myApplicationKey";
        ///    string privateKey = "myPrivateKey";
        ///    string channel = "channel1";
        ///    string message = "The message to send";
        /// 
        ///    bool messageSent = Ibt.Ortc.Api.Ortc.SendMessage(url, isCluster, authenticationToken, applicationKey, privateKey, channel, message)) 
        /// </code>
        /// </example>
        public static bool SendMessage(string url, bool isCluster, string authenticationToken, string applicationKey,
                                       string privateKey, string channel, string message)
        {
            string connectionUrl = url;

            if (String.IsNullOrEmpty(url))
            {
                throw new OrtcEmptyFieldException("Server URL can not be null or empty.");
            }

            if (isCluster)
            {
                connectionUrl = DoGetClusterLogic(url,applicationKey);
            }

            bool ret = false;

            ServicePointManager.ServerCertificateValidationCallback += ValidateRemoteCertificate;

            connectionUrl = connectionUrl.Last() == '/' ? connectionUrl : connectionUrl + "/";
            var request = (HttpWebRequest)WebRequest.Create(String.Format("{0}send", connectionUrl));

            request.Proxy = null;
            request.KeepAlive = false;
            request.Timeout = 15000;
            request.ProtocolVersion = HttpVersion.Version11;
            request.Method = "POST";

            string postParameters = String.Format("AT={0}&AK={1}&PK={2}&C={3}&M={4}", authenticationToken, applicationKey, privateKey, channel, HttpUtility.UrlEncode(message));

            byte[] postBytes = Encoding.UTF8.GetBytes(postParameters);

            request.ContentType = "application/x-www-form-urlencoded";
            request.ContentLength = postBytes.Length;

            HttpWebResponse response = null;

            try
            {
                using (Stream requestStream = request.GetRequestStream())
                {
                    requestStream.Write(postBytes, 0, postBytes.Length);
                    requestStream.Close();
                }

                response = (HttpWebResponse)request.GetResponse();
            }
            catch (WebException ex)
            {
                using (WebResponse errorResponse = ex.Response)
                {
                    var httpResponse = (HttpWebResponse)errorResponse;

                    if (httpResponse != null && httpResponse.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        Stream errorResponseStream = httpResponse.GetResponseStream();
                        if (errorResponseStream != null)
                        {
                            using (var streamReader = new StreamReader(errorResponseStream))
                            {
                                string error = streamReader.ReadToEnd();

                                throw new OrtcAuthenticationNotAuthorizedException(error);
                            }
                        }
                    }
                }

                throw new OrtcNotConnectedException("Unable to connect to the authentication server.");
            }

            if (response != null)
            {
                ret = response.StatusCode == HttpStatusCode.Created;

                response.Close();
            }

            return ret;
        }

        /// <summary>
        /// Gets the subscriptions in the specified channel and if active the first 100 unique metadata.
        /// </summary>
        /// <param name="url">Server containing the presence service.</param>
        /// <param name="isCluster">Specifies if url is cluster.</param>
        /// <param name="applicationKey">Application key with access to presence service.</param>
        /// <param name="authenticationToken">Authentication token with access to presence service.</param>
        /// <param name="channel">Channel with presence data active.</param>
        /// <param name="callback"><see cref="OnPresenceDelegate"/>Callback with error <see cref="OrtcPresenceException"/> and result <see cref="Presence"/>.</param>
        /// <example>
        /// <code>
        /// Ibt.Ortc.Api.Ortc.Presence("http://ortc-developers.realtime.co/server/2.1", true, "myApplicationKey", "myAuthenticationToken", "presence-channel", (error, result) =>
        /// {
        ///     if (error != null)
        ///     {
        ///         Console.WriteLine(error.Message);
        ///     }
        ///     else
        ///     {
        ///         if (result != null)
        ///         {
        ///             Console.WriteLine(result.Subscriptions);
        /// 
        ///             if (result.Metadata != null)
        ///             {
        ///                 foreach (var metadata in result.Metadata)
        ///                 {
        ///                     Console.WriteLine(metadata.Key + " - " + metadata.Value);
        ///                 }
        ///             }
        ///         }
        ///         else
        ///         {
        ///             Console.WriteLine("There is no presence data");
        ///         }
        ///     }
        /// });
        /// </code>
        /// </example>
        public static void Presence(String url, bool isCluster, String applicationKey, String authenticationToken, String channel, OnPresenceDelegate callback)
        {
            Ibt.Ortc.Api.Extensibility.Presence.GetPresence(url, isCluster, applicationKey, authenticationToken, channel, callback);
        }

        /// <summary>
        /// Enables presence for the specified channel with first 100 unique metadata if metadata is set to true.
        /// </summary>
        /// <param name="url">Server containing the presence service.</param>
        /// <param name="isCluster">Specifies if url is cluster.</param>
        /// <param name="applicationKey">Application key with access to presence service.</param>
        /// <param name="privateKey">The private key provided when the ORTC service is purchased.</param>
        /// <param name="channel">Channel to activate presence.</param>
        /// <param name="metadata">Defines if to collect first 100 unique metadata.</param>
        /// <param name="callback">Callback with error <see cref="OrtcPresenceException"/> and result.</param>
        /// <example>
        /// <code>
        /// Ibt.Ortc.Api.Ortc.EnablePresence("http://ortc-developers.realtime.co/server/2.1", true, "myApplicationKey", "myPrivateKey", "presence-channel", false, (error, result) =>
        /// {
        ///     if (error != null)
        ///     {
        ///         Console.WriteLine(error.Message);
        ///     }
        ///     else
        ///     {
        ///         Console.WriteLine(result);
        ///     }
        /// });
        /// </code>
        /// </example>
        public static void EnablePresence(String url, bool isCluster, String applicationKey, String privateKey, String channel, bool metadata, OnEnablePresenceDelegate callback)
        {
            Ibt.Ortc.Api.Extensibility.Presence.EnablePresence(url, isCluster, applicationKey, privateKey, channel, metadata, callback);
        }

        /// <summary>
        /// Disables presence for the specified channel.
        /// </summary>
        /// <param name="url">Server containing the presence service.</param>
        /// <param name="isCluster">Specifies if url is cluster.</param>
        /// <param name="applicationKey">Application key with access to presence service.</param>
        /// <param name="privateKey">The private key provided when the ORTC service is purchased.</param>
        /// <param name="channel">Channel to disable presence.</param>
        /// <param name="callback">Callback with error <see cref="OrtcPresenceException"/> and result.</param>
        /// <example>
        /// <code>
        /// Ibt.Ortc.Api.Ortc.DisablePresence("http://ortc-developers.realtime.co/server/2.1", true, "myApplicationKey", "myPrivateKey", "presence-channel", (error, result) =>
        /// {
        ///     if (error != null)
        ///     {
        ///         Console.WriteLine(error.Message);
        ///     }
        ///     else
        ///     {
        ///         Console.WriteLine(result);
        ///     }
        /// });
        /// </code>
        /// </example>
        public static void DisablePresence(String url, bool isCluster, String applicationKey, String privateKey, String channel, OnDisablePresenceDelegate callback)
        {
            Ibt.Ortc.Api.Extensibility.Presence.DisablePresence(url, isCluster, applicationKey, privateKey, channel, callback);
        }

        #endregion Methods

        #region Private Methods (3)

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        private static bool ValidateRemoteCertificate(object sender, X509Certificate certificate, X509Chain chain,
                                                      SslPolicyErrors policyErrors)
        {
            //return policyErrors == SslPolicyErrors.None;
            return true;
        }

        /// <summary>
        /// Does the get cluster server logic.
        /// </summary>
        /// <param name="url"></param>
        /// <returns></returns>
        private static string DoGetClusterLogic(string url,String applicationKey)
        {
            const int maxConnectionAttempts = 10;
            
            int currentAttempts;
            string connectionUrl = null;

            for (currentAttempts = 0; currentAttempts <= maxConnectionAttempts && String.IsNullOrEmpty(connectionUrl); currentAttempts++)
            {
                connectionUrl = GetClusterServer(url,applicationKey).Trim();

                if (String.IsNullOrEmpty(connectionUrl))
                {
                    currentAttempts++;

                    Thread.Sleep(5000);
                }
            }

            if (currentAttempts > maxConnectionAttempts)
            {
                throw new OrtcNotConnectedException("Unable to connect to the authentication server.");
            }

            return connectionUrl;
        }

        /// <summary>
        /// Gets the cluster server.
        /// </summary>
        /// <returns></returns>
        private static string GetClusterServer(string url, String applicationKey)
        {
            const string responsePattern = "var SOCKET_SERVER = \"(?<host>.*)\";";

            var clusterRequestParameter = !String.IsNullOrEmpty(applicationKey) ? String.Format("appkey={0}",applicationKey) : String.Empty;

            var clusterUrl = String.Format("{0}{1}?{2}",url,!String.IsNullOrEmpty(url) && url[url.Length - 1] != '/' ? "/" : String.Empty,clusterRequestParameter);

            string result = String.Empty;

            try
            {
                var request = (HttpWebRequest)WebRequest.Create(clusterUrl);

                request.Proxy = null;
                request.Timeout = 10000;
                request.ProtocolVersion = HttpVersion.Version11;
                request.Method = "GET";

                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls;

                var response = (HttpWebResponse) request.GetResponse();

                Stream responseStream = response.GetResponseStream();

                if (responseStream != null)
                {
                    var responseReader = new StreamReader(responseStream);

                    string responseText = responseReader.ReadToEnd();

                    Match match = Regex.Match(responseText, responsePattern);

                    result = match.Groups["host"].Value;

                    responseStream.Close();
                }
            }
            catch (Exception)
            {
                //NOTE: If error will occur it means that was not possible to get a server from the cluster
            }

            return result;
        }

        #endregion
    }
}