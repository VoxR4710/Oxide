﻿﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Libraries;
using Oxide.Game.Rust.Libraries.Covalence;
using UnityEngine;

namespace Oxide.Game.Rust
{
    /// <summary>
    /// The core Rust plugin
    /// </summary>
    public partial class RustCore : CSPlugin
    {
        #region Initialization

        /// <summary>
        /// Initializes a new instance of the RustCore class
        /// </summary>
        public RustCore()
        {
            // Set plugin info attributes
            Title = "Rust";
            Author = "Oxide Team";
            var assemblyVersion = RustExtension.AssemblyVersion;
            Version = new VersionNumber(assemblyVersion.Major, assemblyVersion.Minor, assemblyVersion.Build);
        }

        // Libraries
        internal readonly Command cmdlib = Interface.Oxide.GetLibrary<Command>();
        internal readonly Lang lang = Interface.Oxide.GetLibrary<Lang>();
        internal readonly Permission permission = Interface.Oxide.GetLibrary<Permission>();
        internal readonly Player Player = Interface.Oxide.GetLibrary<Player>();

        // Instances
        internal static readonly RustCovalenceProvider Covalence = RustCovalenceProvider.Instance;
        internal readonly PluginManager pluginManager = Interface.Oxide.RootPluginManager;
        internal readonly IServer Server = Covalence.CreateServer();

        // Commands that a plugin can't override
        internal static IEnumerable<string> RestrictedCommands => new[]
        {
            "ownerid", "moderatorid"
        };

        internal bool serverInitialized;

        /// <summary>
        /// Checks if the permission system has loaded, shows an error if it failed to load
        /// </summary>
        /// <param name="player"></param>
        /// <returns></returns>
        private bool PermissionsLoaded(IPlayer player)
        {
            if (permission.IsLoaded) return true;
            player.Reply(lang.GetMessage("PermissionsNotLoaded", this, player.Id), permission.LastException.Message);
            return false;
        }

        #endregion

        #region Core Hooks

        /// <summary>
        /// Called when the plugin is initializing
        /// </summary>
        [HookMethod("Init")]
        private void Init()
        {
            // Configure remote error logging
            RemoteLogger.SetTag("game", Title.ToLower());
            RemoteLogger.SetTag("game version", Server.Version);

            // Add core general commands
            AddCovalenceCommand(new[] { "oxide.lang", "lang" }, "LangCommand");
            AddCovalenceCommand(new[] { "oxide.version", "version" }, "VersionCommand");

            // Add core plugin commands
            AddCovalenceCommand(new[] { "oxide.plugins", "plugins" }, "PluginsCommand");
            AddCovalenceCommand(new[] { "oxide.load", "load" }, "LoadCommand");
            AddCovalenceCommand(new[] { "oxide.reload", "reload" }, "ReloadCommand");
            AddCovalenceCommand(new[] { "oxide.unload", "unload" }, "UnloadCommand");

            // Add core permission commands
            AddCovalenceCommand(new[] { "oxide.grant", "grant" }, "GrantCommand");
            AddCovalenceCommand(new[] { "oxide.group", "group" }, "GroupCommand");
            AddCovalenceCommand(new[] { "oxide.revoke", "revoke" }, "RevokeCommand");
            AddCovalenceCommand(new[] { "oxide.show", "show" }, "ShowCommand");
            AddCovalenceCommand(new[] { "oxide.usergroup", "usergroup" }, "UserGroupCommand");

            // Register core permissions
            permission.RegisterPermission("oxide.plugins", this);
            permission.RegisterPermission("oxide.load", this);
            permission.RegisterPermission("oxide.reload", this);
            permission.RegisterPermission("oxide.unload", this);
            permission.RegisterPermission("oxide.grant", this);
            permission.RegisterPermission("oxide.group", this);
            permission.RegisterPermission("oxide.revoke", this);
            permission.RegisterPermission("oxide.show", this);
            permission.RegisterPermission("oxide.usergroup", this);

            // Register messages for localization
            foreach (var language in Localziation.languages) lang.RegisterMessages(language.Value, this, language.Key);

            // Setup default permission groups
            if (permission.IsLoaded)
            {
                var rank = 0;
                foreach (var defaultGroup in Interface.Oxide.Config.Options.DefaultGroups)
                    if (!permission.GroupExists(defaultGroup)) permission.CreateGroup(defaultGroup, defaultGroup, rank++);

                permission.RegisterValidate(s =>
                {
                    ulong temp;
                    if (!ulong.TryParse(s, out temp)) return false;
                    var digits = temp == 0 ? 1 : (int)Math.Floor(Math.Log10(temp) + 1);
                    return digits >= 17;
                });

                permission.CleanUp();
            }
        }

        /// <summary>
        /// Called when another plugin has been loaded
        /// </summary>
        /// <param name="plugin"></param>
        [HookMethod("OnPluginLoaded")]
        private void OnPluginLoaded(Plugin plugin)
        {
            // Call OnServerInitialized for hotloaded plugins
            if (serverInitialized) plugin.CallHook("OnServerInitialized");
        }

        /// <summary>
        /// Called when the server is first initialized
        /// </summary>
        [HookMethod("OnServerInitialized")]
        private void OnServerInitialized()
        {
            if (serverInitialized) return;

            if (Interface.Oxide.CheckConsole() && ServerConsole.Instance != null)
            {
                ServerConsole.Instance.enabled = false;
                UnityEngine.Object.Destroy(ServerConsole.Instance);
                typeof(SingletonComponent<ServerConsole>).GetField("instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);
            }

            Analytics.Collect();
            RustExtension.ServerConsole();

            if (!Interface.Oxide.Config.Options.Modded)
                Interface.Oxide.LogWarning("The server is currently listed under Community. Please be aware that Facepunch only allows admin tools" +
                                           "(that do not affect gameplay) under the Community section");

            serverInitialized = true;
        }

        /// <summary>
        /// Called when the server is shutting down
        /// </summary>
        [HookMethod("OnServerShutdown")]
        private void OnServerShutdown() => Interface.Oxide.OnShutdown();

        #endregion

        #region Command Handling

        /// <summary>
        /// Parses the specified command
        /// </summary>
        /// <param name="argstr"></param>
        /// <param name="command"></param>
        /// <param name="args"></param>
        private void ParseCommand(string argstr, out string command, out string[] args)
        {
            var arglist = new List<string>();
            var sb = new StringBuilder();
            var inlongarg = false;

            foreach (var c in argstr)
            {
                if (c == '"')
                {
                    if (inlongarg)
                    {
                        var arg = sb.ToString().Trim();
                        if (!string.IsNullOrEmpty(arg)) arglist.Add(arg);
                        sb.Clear();
                        inlongarg = false;
                    }
                    else
                        inlongarg = true;
                }
                else if (char.IsWhiteSpace(c) && !inlongarg)
                {
                    var arg = sb.ToString().Trim();
                    if (!string.IsNullOrEmpty(arg)) arglist.Add(arg);
                    sb.Clear();
                }
                else
                    sb.Append(c);
            }

            if (sb.Length > 0)
            {
                var arg = sb.ToString().Trim();
                if (!string.IsNullOrEmpty(arg)) arglist.Add(arg);
            }

            if (arglist.Count == 0)
            {
                command = null;
                args = null;
                return;
            }

            command = arglist[0];
            arglist.RemoveAt(0);
            args = arglist.ToArray();
        }

        /// <summary>
        /// Called when a server command was run
        /// </summary>
        /// <param name="arg"></param>
        /// <returns></returns>
        [HookMethod("IOnServerCommand")]
        private object IOnServerCommand(ConsoleSystem.Arg arg)
        {
            if (arg?.cmd == null) return null;
            if (Interface.Call("OnServerCommand", arg) != null) return true;

            // Get the args
            var str = arg.GetString(0);
            if (string.IsNullOrEmpty(str)) return null;

            // Check if command is from a player
            var player = arg.Connection?.player as BasePlayer;
            if (player == null) return null;

            // Get the full command
            var message = str.TrimStart('/');

            // Parse it
            string cmd;
            string[] args;
            ParseCommand(message, out cmd, out args);
            if (cmd == null) return null;

            // Get the covalence player
            var iplayer = player.IPlayer;
            if (iplayer == null) return null;

            // Is the command blocked?
            var blockedSpecific = Interface.Call("OnPlayerCommand", arg);
            var blockedCovalence = Interface.Call("OnUserCommand", iplayer, cmd, args);
            if (blockedSpecific != null || blockedCovalence != null) return true;

            // Is it a chat command?
            if (arg.cmd.FullName != "chat.say") return null;
            if (str[0] != '/') return null; // TODO: Return if no arguments given

            // Is it a valid chat command?
            if (!Covalence.CommandSystem.HandleChatMessage(iplayer, str) && !cmdlib.HandleChatCommand(player, cmd, args))
            {
                if (!Interface.Oxide.Config.Options.Modded) return null;

                iplayer.Reply(lang.GetMessage("UnknownCommand", this, iplayer.Id), cmd);
                arg.ReplyWith(string.Empty);
                return true;
            }

            return null;
        }

        /// <summary>
        /// Returns the BasePlayer for the specified name, ID, or IP address string
        /// </summary>
        /// <param name="nameOrIdOrIp"></param>
        /// <returns></returns>
        public static BasePlayer FindPlayer(string nameOrIdOrIp)
        {
            BasePlayer player = null;
            foreach (var activePlayer in BasePlayer.activePlayerList)
            {
                if (string.IsNullOrEmpty(activePlayer.UserIDString)) continue;
                if (activePlayer.UserIDString.Equals(nameOrIdOrIp))
                    return activePlayer;
                if (string.IsNullOrEmpty(activePlayer.displayName)) continue;
                if (activePlayer.displayName.Equals(nameOrIdOrIp, StringComparison.OrdinalIgnoreCase))
                    return activePlayer;
                if (activePlayer.displayName.Contains(nameOrIdOrIp, CompareOptions.OrdinalIgnoreCase))
                    player = activePlayer;
                if (activePlayer.net?.connection != null && activePlayer.net.connection.ipaddress.Equals(nameOrIdOrIp))
                    return activePlayer;
            }
            foreach (var sleepingPlayer in BasePlayer.sleepingPlayerList)
            {
                if (string.IsNullOrEmpty(sleepingPlayer.UserIDString)) continue;
                if (sleepingPlayer.UserIDString.Equals(nameOrIdOrIp))
                    return sleepingPlayer;
                if (string.IsNullOrEmpty(sleepingPlayer.displayName)) continue;
                if (sleepingPlayer.displayName.Equals(nameOrIdOrIp, StringComparison.OrdinalIgnoreCase))
                    return sleepingPlayer;
                if (sleepingPlayer.displayName.Contains(nameOrIdOrIp, CompareOptions.OrdinalIgnoreCase))
                    player = sleepingPlayer;
            }
            return player;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Returns the BasePlayer for the specified name string
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public static BasePlayer FindPlayerByName(string name)
        {
            BasePlayer player = null;
            foreach (var activePlayer in BasePlayer.activePlayerList)
            {
                if (string.IsNullOrEmpty(activePlayer.displayName)) continue;
                if (activePlayer.displayName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return activePlayer;
                if (activePlayer.displayName.Contains(name, CompareOptions.OrdinalIgnoreCase))
                    player = activePlayer;
            }
            foreach (var sleepingPlayer in BasePlayer.sleepingPlayerList)
            {
                if (string.IsNullOrEmpty(sleepingPlayer.displayName)) continue;
                if (sleepingPlayer.displayName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return sleepingPlayer;
                if (sleepingPlayer.displayName.Contains(name, CompareOptions.OrdinalIgnoreCase))
                    player = sleepingPlayer;
            }
            return player;
        }

        /// <summary>
        /// Returns the BasePlayer for the specified ID ulong
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public static BasePlayer FindPlayerById(ulong id)
        {
            foreach (var activePlayer in BasePlayer.activePlayerList)
            {
                if (activePlayer.userID == id)
                    return activePlayer;
            }
            foreach (var sleepingPlayer in BasePlayer.sleepingPlayerList)
            {
                if (sleepingPlayer.userID == id)
                    return sleepingPlayer;
            }
            return null;
        }

        /// <summary>
        /// Returns the BasePlayer for the specified ID string
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public static BasePlayer FindPlayerByIdString(string id)
        {
            foreach (var activePlayer in BasePlayer.activePlayerList)
            {
                if (string.IsNullOrEmpty(activePlayer.UserIDString)) continue;
                if (activePlayer.UserIDString.Equals(id))
                    return activePlayer;
            }
            foreach (var sleepingPlayer in BasePlayer.sleepingPlayerList)
            {
                if (string.IsNullOrEmpty(sleepingPlayer.UserIDString)) continue;
                if (sleepingPlayer.UserIDString.Equals(id))
                    return sleepingPlayer;
            }
            return null;
        }

        #endregion
    }
}
