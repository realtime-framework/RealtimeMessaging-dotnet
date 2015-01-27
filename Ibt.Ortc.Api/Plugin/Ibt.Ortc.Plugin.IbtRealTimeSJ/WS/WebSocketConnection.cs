using System;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Ibt.Ortc.Api.Extensibility;
using WebSocket4Net;

namespace Ibt.Ortc.Plugin.IbtRealTimeSJ.WS
{
    class WebSocketConnection
    {
        #region Attributes (1)

        WebSocket _websocket = null;

        #endregion

        #region Methods - Public (3)

        public void Connect(string url)
        {
            Uri uri = null;

            string connectionId = Strings.RandomString(8);
            int serverId = Strings.RandomNumber(1, 1000);

            try
            {
                uri = new Uri(url);
            }
            catch (Exception)
            {
                throw new OrtcEmptyFieldException(String.Format("Invalid URL: {0}", url));
            }

            string prefix = uri != null && "https".Equals(uri.Scheme) ? "wss" : "ws";

            Uri connectionUrl = new Uri(String.Format("{0}://{1}:{2}/broadcast/{3}/{4}/websocket", prefix, uri.DnsSafeHost, uri.Port, serverId, connectionId));

            //
            // NOTE: For wss connections, must have a valid installed certificate
            // See: http://www.runcode.us/q/c-iphone-push-server
            //

            _websocket = new WebSocket(connectionUrl.AbsoluteUri);

            _websocket.Opened += new EventHandler(websocket_Opened);
            _websocket.Error += new EventHandler<SuperSocket.ClientEngine.ErrorEventArgs>(websocket_Error);
            _websocket.Closed += new EventHandler(websocket_Closed);
            _websocket.MessageReceived += new EventHandler<MessageReceivedEventArgs>(websocket_MessageReceived);

            _websocket.Open();
        }

        public void Close()
        {
            if (_websocket != null)
            {
                if (_websocket.State != WebSocketState.Connecting)
                {
                    _websocket.Close();
                }
            }
        }

        public void Send(string message)
        {
            if (_websocket != null)
            {
                _websocket.Send(Serialize(message));
            }
        }

        #endregion

        #region Methods - Private (1)

        /// <summary>
        /// Serializes the specified data.
        /// </summary>
        /// <param name="data">The data.</param>
        /// <returns></returns>
        private string Serialize(object data)
        {
            string result = "";

            try
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(data.GetType());
                var stream = new System.IO.MemoryStream();
                serializer.WriteObject(stream, data);
                string jsonData = Encoding.UTF8.GetString(stream.ToArray(), 0, (int)stream.Length);
                stream.Close();
                result = jsonData;
            }
            catch
            {
            }

            return result;
        }

        #endregion

        #region Delegates (4)

        public delegate void onOpenedDelegate();
        public delegate void onClosedDelegate();
        public delegate void onErrorDelegate(Exception error);
        public delegate void onMessageReceivedDelegate(string message);

        #endregion

        #region Events (4)

        public event onOpenedDelegate OnOpened;
        public event onClosedDelegate OnClosed;
        public event onErrorDelegate OnError;
        public event onMessageReceivedDelegate OnMessageReceived;

        #endregion

        #region Events Handles (4)

        private void websocket_MessageReceived(object sender, MessageReceivedEventArgs e)
        {
            var ev = OnMessageReceived;

            if (ev != null)
            {
                Task.Factory.StartNew(() => ev(e.Message));
            }
        }

        private void websocket_Opened(object sender, EventArgs e)
        {
            var ev = OnOpened;

            if (ev != null)
            {
                Task.Factory.StartNew(() => ev());
            }
        }

        private void websocket_Closed(object sender, EventArgs e)
        {
            var ev = OnClosed;

            if (ev != null)
            {
                Task.Factory.StartNew(() => ev());
            }
        }

        private void websocket_Error(object sender, SuperSocket.ClientEngine.ErrorEventArgs e)
        {
            var ev = OnError;

            if (ev != null)
            {
                Task.Factory.StartNew(() => ev(e.Exception));
            }
        }

        #endregion
    }
}
