namespace Ibt.Ortc.Api.Extensibility
{
    public class ChannelSubscription
    {
        public ChannelSubscription()
        {

        }

        private bool isSubscribing;
        public bool IsSubscribing
        {
            get { return isSubscribing; }
            set
            {
                if (value)
                {
                    isSubscribed = false;
                }
                isSubscribing = value;
            }
        }

        private bool isSubscribed;
        public bool IsSubscribed
        {
            get { return isSubscribed; }
            set
            {
                if (value)
                {
                    isSubscribing = false;
                }
                isSubscribed = value;
            }
        }

        public bool SubscribeOnReconnected { get; set; }
        public OnMessageDelegate OnMessage { get; set; }

        public ChannelSubscription(bool subscribeOnReconnected, OnMessageDelegate onMessage)
        {
            this.SubscribeOnReconnected = subscribeOnReconnected;
            this.OnMessage = onMessage;
            this.IsSubscribed = false;
            this.IsSubscribing = false;
        }
    }
}
