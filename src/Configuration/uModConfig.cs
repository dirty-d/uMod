﻿extern alias References;

using References::Newtonsoft.Json;
using System.Collections;
using System.Collections.Generic;

namespace uMod.Configuration
{
    /// <summary>
    /// Represents all uMod config settings
    /// </summary>
    public class uModConfig : ConfigFile
    {
        /// <summary>
        /// Settings for the modded server
        /// </summary>
        public class uModOptions
        {
            public bool Modded;
            public bool PluginWatchers;
            public string[] PluginDirectories;
            public DefaultGroups DefaultGroups;
        }

        [JsonObject]
        public class DefaultGroups : IEnumerable<string>
        {
            public string Players;
            public string Administrators;

            public IEnumerator<string> GetEnumerator()
            {
                yield return Players;
                yield return Administrators;
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        /// <summary>
        /// Settings for the custom uMod console
        /// </summary>
        public class uModConsole
        {
            /// <summary>
            /// Gets or sets if the uMod console should be setup
            /// </summary>
            public bool Enabled { get; set; }

            /// <summary>
            /// Gets or sets if the uMod console should run in minimalist mode (no tags in the console)
            /// </summary>
            public bool MinimalistMode { get; set; }

            /// <summary>
            /// Gets or sets if the uMod console should show the toolbar on the bottom with server information
            /// </summary>
            public bool ShowStatusBar { get; set; }
        }

        /// <summary>
        /// Settings for the custom uMod remote console
        /// </summary>
        public class uModRcon
        {
            /// <summary>
            /// Gets or sets if the uMod remote console should be setup
            /// </summary>
            public bool Enabled { get; set; }

            /// <summary>
            /// Gets or sets the uMod remote console port
            /// </summary>
            public int Port { get; set; }

            /// <summary>
            /// Gets or sets the uMod remote console password
            /// </summary>
            public string Password { get; set; }

            /// <summary>
            /// Gets or sets the uMod remote console chat prefix
            /// </summary>
            public string ChatPrefix { get; set; }
        }

        /// <summary>
        /// Gets or sets information regarding the uMod options
        /// </summary>
        public uModOptions Options { get; set; }

        /// <summary>
        /// Gets or sets information regarding the uMod console
        /// </summary>
        [JsonProperty(PropertyName = "uModConsole")]
        public uModConsole Console { get; set; }

        /// <summary>
        /// Gets or sets information regarding the uMod remote console
        /// </summary>
        [JsonProperty(PropertyName = "uModRcon")]
        public uModRcon Rcon { get; set; }

        /// <summary>
        /// Sets defaults for uMod configuration
        /// </summary>
        public uModConfig(string filename) : base(filename)
        {
            Options = new uModOptions
            {
                Modded = true,
                PluginWatchers = true,
                PluginDirectories = new[] { "universal" },
                DefaultGroups = new DefaultGroups { Administrators = "admin", Players = "default" }
            };
            Console = new uModConsole { Enabled = true, MinimalistMode = true, ShowStatusBar = true };
            Rcon = new uModRcon { Enabled = false, ChatPrefix = "[Server Console]", Port = 25580, Password = string.Empty };
        }
    }
}
