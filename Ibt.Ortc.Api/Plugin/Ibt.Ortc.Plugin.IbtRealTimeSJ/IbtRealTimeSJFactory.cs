using Ibt.Ortc.Api.Extensibility;
using System.Collections.Generic;
using System.Reflection;
using System;
using System.IO;

namespace Ibt.Ortc.Plugin.IbtRealTimeSJ
{
    /// <summary>
    /// IBT Real Time SJ type factory.
    /// </summary>
    /// <remarks>
    /// Factory Type = IbtRealTimeSJ.
    /// </remarks>
    [ExportOrtcFactory(FactoryType = "IbtRealTimeSJ")]
    public class IbtRealTimeSJFactory : IOrtcFactory
    {
        #region Attributes

        /// <summary>
        /// To load the assemblies just once.
        /// </summary>
        private readonly IDictionary<string, Assembly> _loadedAssemblies;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="IbtRealTimeKFactory"/> class.
        /// </summary>
        public IbtRealTimeSJFactory()
        {
            _loadedAssemblies = new Dictionary<string, Assembly>();

            //LoadWebSocket4NetAssembly();
        }

        #endregion

        #region Methods

        /// <summary>
        /// Creates a new instance of a ortc client.
        /// </summary>
        /// <returns>
        /// New instance of <see cref="OrtcClient"/>.
        /// </returns>
        public OrtcClient CreateClient()
        {
            return new IbtRealTimeSJClient();
        }

        /// <summary>
        /// Loads the WebSocket4Net assembly so the client do not need to add a project reference to this dll.
        /// </summary>
        private void LoadWebSocket4NetAssembly()
        {
            string[] test = Assembly.GetExecutingAssembly().GetManifestResourceNames();

            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string resourceName = Assembly.GetExecutingAssembly().GetName().Name + ".Lib." + new AssemblyName(args.Name).Name + ".dll";

                if (!_loadedAssemblies.ContainsKey(resourceName))
                {
                    lock (_loadedAssemblies)
                    {
                        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                        {
                            if (stream != null)
                            {
                                var assemblyData = new byte[stream.Length];

                                stream.Read(assemblyData, 0, assemblyData.Length);

                                Assembly newAssembly = Assembly.Load(assemblyData);

                                _loadedAssemblies.Add(resourceName, newAssembly);
                            }
                        }
                    }
                }

                return _loadedAssemblies.ContainsKey(resourceName) ? _loadedAssemblies[resourceName] : null;
            };
        }

        #endregion
    }
}