﻿extern alias References;

using References::ProtoBuf;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using uMod.Plugins;

namespace uMod.Libraries
{
    /// <summary>
    /// Contains all data for a specified user
    /// </summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
    public class UserData
    {
        /// <summary>
        /// Gets or sets the last seen nickname for this player
        /// </summary>
        public string LastSeenNickname { get; set; } = "Unnamed";

        /// <summary>
        /// Gets or sets the individual permissions for this player
        /// </summary>
        public HashSet<string> Perms { get; set; } = new HashSet<string>();

        /// <summary>
        /// Gets or sets the group for this player
        /// </summary>
        public HashSet<string> Groups { get; set; } = new HashSet<string>();
    }

    /// <summary>
    /// Contains all data for a specified group
    /// </summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
    public class GroupData
    {
        /// <summary>
        /// Gets or sets the title of this group
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the rank of this group
        /// </summary>
        public int Rank { get; set; }

        /// <summary>
        /// Gets or sets the individual permissions for this group
        /// </summary>
        public HashSet<string> Perms { get; set; } = new HashSet<string>();

        /// <summary>
        /// Gets or sets the parent for this group
        /// </summary>
        public string ParentGroup { get; set; } = string.Empty;
    }

    /// <summary>
    /// A library providing a unified permissions system
    /// </summary>
    public class Permission : Library
    {
        // All registered permissions
        private readonly Dictionary<Plugin, HashSet<string>> permset;

        // All user data
        private Dictionary<string, UserData> userdata;

        // All group data
        private Dictionary<string, GroupData> groupdata;

        private Func<string, bool> validate;

        // Permission status
        public bool IsLoaded { get; private set; }

        /// <summary>
        /// Initializes a new instance of the Permission library
        /// </summary>
        public Permission()
        {
            // Initialize
            permset = new Dictionary<Plugin, HashSet<string>>();

            // Load the datafile
            LoadFromDatafile();
        }

        /// <summary>
        /// Loads all permissions data from the datafile
        /// </summary>
        private void LoadFromDatafile()
        {
            // Initialize
            Utility.DatafileToProto<Dictionary<string, UserData>>("umod.users");
            Utility.DatafileToProto<Dictionary<string, GroupData>>("umod.groups");
            userdata = ProtoStorage.Load<Dictionary<string, UserData>>("umod.users") ?? new Dictionary<string, UserData>();
            groupdata = ProtoStorage.Load<Dictionary<string, GroupData>>("umod.groups") ?? new Dictionary<string, GroupData>();
            foreach (KeyValuePair<string, GroupData> pair in groupdata)
            {
                if (!string.IsNullOrEmpty(pair.Value.ParentGroup) && HasCircularParent(pair.Key, pair.Value.ParentGroup))
                {
                    Interface.uMod.LogWarning($"Detected circular parent group for '{pair.Key}'! Removing parent '{pair.Value.ParentGroup}'");
                    pair.Value.ParentGroup = null;
                }
            }
            IsLoaded = true;
        }

        /// <summary>
        /// Exports user/group data to json
        /// </summary>
        [LibraryFunction("Export")]
        public void Export(string prefix = "auth")
        {
            if (IsLoaded)
            {
                Interface.uMod.DataFileSystem.WriteObject(prefix + ".groups", groupdata);
                Interface.uMod.DataFileSystem.WriteObject(prefix + ".users", userdata);
            }
        }

        /// <summary>
        /// Saves all permissions data to the data files
        /// </summary>
        public void SaveData()
        {
            SaveUsers();
            SaveGroups();
        }

        /// <summary>
        /// Saves users permissions data to the data file
        /// </summary>
        public void SaveUsers() => ProtoStorage.Save(userdata, "umod.users");

        /// <summary>
        /// Saves groups permissions data to the data file
        /// </summary>
        public void SaveGroups() => ProtoStorage.Save(groupdata, "umod.groups");

        /// <summary>
        /// Register user ID validation
        /// </summary>
        /// <param name="val"></param>
        public void RegisterValidate(Func<string, bool> val) => validate = val;

        /// <summary>
        /// Clean invalid user ID entries
        /// </summary>
        public void CleanUp()
        {
            if (IsLoaded && validate != null)
            {
                string[] invalid = userdata.Keys.Where(k => !validate(k)).ToArray();
                if (invalid.Length > 0)
                {
                    foreach (string i in invalid)
                    {
                        userdata.Remove(i);
                    }
                }
            }
        }

        /// <summary>
        /// Migrate permissions from one group to another
        /// </summary>
        public void MigrateGroup(string oldGroup, string newGroup)
        {
            if (IsLoaded)
            {
                if (GroupExists(oldGroup))
                {
                    string groups = ProtoStorage.GetFileDataPath("umod.groups.data");
                    File.Copy(groups, groups + ".old", true);

                    foreach (string perm in GetGroupPermissions(oldGroup))
                    {
                        GrantGroupPermission(newGroup, perm, null);
                    }

                    if (GetUsersInGroup(oldGroup).Length == 0)
                    {
                        RemoveGroup(oldGroup);
                    }
                }
            }
        }

        #region Permission Management

        /// <summary>
        /// Registers the specified permission
        /// </summary>
        /// <param name="name"></param>
        /// <param name="owner"></param>
        [LibraryFunction("RegisterPermission")]
        public void RegisterPermission(string name, Plugin owner)
        {
            if (!string.IsNullOrEmpty(name))
            {
                name = name.ToLower();

                if (PermissionExists(name))
                {
                    Interface.uMod.LogWarning($"Duplicate permission registered '{name}' (by plugin '{owner.Title}')");
                    return;
                }

                string prefix = owner.Name.ToLower() + ".";
                if (!name.StartsWith(prefix) && !owner.IsCorePlugin)
                {
                    Interface.uMod.LogWarning($"Missing plugin name prefix '{prefix}' for permission '{name}' (by plugin '{owner.Title}')");
                    return;
                }

                if (!permset.TryGetValue(owner, out HashSet<string> set))
                {
                    set = new HashSet<string>();
                    permset.Add(owner, set);
                    owner.OnRemovedFromManager.Add(owner_OnRemovedFromManager);
                }

                set.Add(name);

                Interface.CallHook("OnPermissionRegistered", name, owner);
            }
        }

        /// <summary>
        /// Returns if the specified permission exists or not
        /// </summary>
        /// <param name="name"></param>
        /// <param name="owner"></param>
        /// <returns></returns>
        [LibraryFunction("PermissionExists")]
        public bool PermissionExists(string name, Plugin owner = null)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            name = name.ToLower();

            if (owner == null)
            {
                if (permset.Count > 0)
                {
                    if (name.Equals("*"))
                    {
                        return true;
                    }

                    if (name.EndsWith("*"))
                    {
                        name = name.TrimEnd('*');
                        return permset.Values.SelectMany(v => v).Any(p => p.StartsWith(name));
                    }
                }
                return permset.Values.Any(v => v.Contains(name));
            }

            if (!permset.TryGetValue(owner, out HashSet<string> set))
            {
                return false;
            }

            if (set.Count > 0)
            {
                if (name.Equals("*"))
                {
                    return true;
                }

                if (name.EndsWith("*"))
                {
                    name = name.TrimEnd('*');
                    return set.Any(p => p.StartsWith(name));
                }
            }
            return set.Contains(name);
        }

        #endregion Permission Management

        /// <summary>
        /// Called when a plugin has been unloaded
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="manager"></param>
        private void owner_OnRemovedFromManager(Plugin sender, PluginManager manager) => permset.Remove(sender);

        #region Querying

        /// <summary>
        /// Returns if the specified user id is valid
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [LibraryFunction("UserIdValid")]
        public bool UserIdValid(string id) => validate == null || validate(id);

        /// <summary>
        /// Returns if the specified user exists
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [LibraryFunction("UserExists")]
        public bool UserExists(string id) => userdata.ContainsKey(id);

        /// <summary>
        /// Returns the data for the specified user
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public UserData GetUserData(string id)
        {
            if (!userdata.TryGetValue(id, out UserData data))
            {
                userdata.Add(id, data = new UserData());
            }

            // Return the data
            return data;
        }

        /// <summary>
        /// Updates the nickname
        /// </summary>
        /// <param name="id"></param>
        /// <param name="nickname"></param>
        [LibraryFunction("UpdateNickname")]
        public void UpdateNickname(string id, string nickname)
        {
            if (UserExists(id))
            {
                UserData data = GetUserData(id);
                string oldName = data.LastSeenNickname;
                string newName = nickname.Sanitize();
                data.LastSeenNickname = nickname.Sanitize();

                Interface.CallHook("OnUserNameUpdated", id, oldName, newName);
            }
        }

        /// <summary>
        /// Check if user has a group
        /// </summary>
        /// <param name="id"></param>
        [LibraryFunction("UserHasAnyGroup")]
        public bool UserHasAnyGroup(string id) => UserExists(id) && GetUserData(id).Groups.Count > 0;

        /// <summary>
        /// Returns if the specified group has the specified permission or not
        /// </summary>
        /// <param name="groups"></param>
        /// <param name="perm"></param>
        /// <returns></returns>
        [LibraryFunction("GroupsHavePermission")]
        public bool GroupsHavePermission(HashSet<string> groups, string perm) => groups.Any(@group => GroupHasPermission(@group, perm));

        /// <summary>
        /// Returns if the specified group has the specified permission or not
        /// </summary>
        /// <param name="name"></param>
        /// <param name="perm"></param>
        /// <returns></returns>
        [LibraryFunction("GroupHasPermission")]
        public bool GroupHasPermission(string name, string perm)
        {
            if (!GroupExists(name) || string.IsNullOrEmpty(perm))
            {
                return false;
            }

            if (!groupdata.TryGetValue(name.ToLower(), out GroupData group))
            {
                return false;
            }

            // Check if the group has the perm
            return group.Perms.Contains(perm.ToLower()) || GroupHasPermission(group.ParentGroup, perm);
        }

        /// <summary>
        /// Checks if specified permission belongs to group or parent group
        /// </summary>
        /// <param name="name"></param>
        /// <param name="perm"></param>
        /// <returns></returns>
        [LibraryFunction("IsGroupPermissionInherited")]
        public bool IsGroupPermissionInherited(string name, string perm)
        {
            if (!GroupHasPermission(name, perm))
            {
                return false;
            }

            if (!groupdata.TryGetValue(name.ToLower(), out GroupData group))
            {
                return false;
            }

            return !group.Perms.Contains(perm.ToLower());
        }

        /// <summary>
        /// Returns the parent group that the specified permission belongs to
        /// </summary>
        /// <param name="name"></param>
        /// <param name="perm"></param>
        /// <returns></returns>
        [LibraryFunction("GetGroupPermissionGroup")]
        public string GetGroupPermissionGroup(string name, string perm)
        {
            if (!GroupExists(name) || string.IsNullOrEmpty(perm))
            {
                return null;
            }

            if (!groupdata.TryGetValue(name.ToLower(), out GroupData group))
            {
                return null;
            }

            if (group.Perms.Contains(perm.ToLower()))
            {
                return name;
            }

            return GetGroupPermissionGroup(group.ParentGroup, perm);
        }

        /// <summary>
        /// Returns if the specified user has the specified permission
        /// </summary>
        /// <param name="id"></param>
        /// <param name="perm"></param>
        /// <returns></returns>
        [LibraryFunction("UserHasPermission")]
        public bool UserHasPermission(string id, string perm)
        {
            if (string.IsNullOrEmpty(perm))
            {
                return false;
            }

            // Always allow the server console
            if (id.Equals("server_console"))
            {
                return true;
            }

            perm = perm.ToLower();

            // First, get the player data
            UserData data = GetUserData(id);

            // Check if they have the perm
            if (data.Perms.Contains(perm))
            {
                return true;
            }

            // Check if their group has the perm
            return GroupsHavePermission(data.Groups, perm);
        }

        /// <summary>
        /// Returns if the specified user permission is inherited from a group
        /// </summary>
        /// <param name="id"></param>
        /// <param name="perm"></param>
        /// <returns></returns>
        [LibraryFunction("IsUserPermissionInherited")]
        public bool IsUserPermissionInherited(string id, string perm)
        {
            if (string.IsNullOrEmpty(perm))
            {
                return false;
            }

            perm = perm.ToLower();

            // First, get the player data
            UserData data = GetUserData(id);

            // Check if they have the perm
            if (data.Perms.Contains(perm))
            {
                return false;
            }

            // Check if their group has the perm
            return GroupsHavePermission(data.Groups, perm);
        }

        /// <summary>
        /// Returns the group that a specified user permission is inherited from
        /// </summary>
        /// <param name="id"></param>
        /// <param name="perm"></param>
        /// <returns></returns>
        [LibraryFunction("GetUserPermissionGroup")]
        public string GetUserPermissionGroup(string id, string perm)
        {
            if (string.IsNullOrEmpty(perm))
            {
                return null;
            }

            perm = perm.ToLower();

            // First, get the player data
            UserData data = GetUserData(id);

            // Check if they have the perm
            if (data.Perms.Contains(perm))
            {
                return null;
            }

            foreach(string group in data.Groups)
            {
                string permissionGroup = GetGroupPermissionGroup(group, perm);
                if (!string.IsNullOrEmpty(permissionGroup))
                {
                    return permissionGroup;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns the group to which the specified user belongs
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [LibraryFunction("GetUserGroups")]
        public string[] GetUserGroups(string id) => GetUserData(id).Groups.ToArray();

        /// <summary>
        /// Returns the permissions which the specified user has
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [LibraryFunction("GetUserPermissions")]
        public string[] GetUserPermissions(string id)
        {
            UserData data = GetUserData(id);
            List<string> perms = data.Perms.ToList();
            foreach (string group in data.Groups)
            {
                perms.AddRange(GetGroupPermissions(group));
            }

            return new HashSet<string>(perms).ToArray();
        }

        /// <summary>
        /// Returns the permissions which the specified group has, with optional transversing of parent groups
        /// </summary>
        /// <param name="name"></param>
        /// <param name="parents"></param>
        /// <returns></returns>
        [LibraryFunction("GetGroupPermissions")]
        public string[] GetGroupPermissions(string name, bool parents = false)
        {
            if (!GroupExists(name))
            {
                return new string[0];
            }

            if (!groupdata.TryGetValue(name.ToLower(), out GroupData group))
            {
                return new string[0];
            }

            List<string> perms = group.Perms.ToList();
            if (parents)
            {
                perms.AddRange(GetGroupPermissions(group.ParentGroup));
            }

            return new HashSet<string>(perms).ToArray();
        }

        /// <summary>
        /// Returns the permissions which are registered
        /// </summary>
        /// <returns></returns>
        [LibraryFunction("GetPermissions")]
        public string[] GetPermissions() => new HashSet<string>(permset.Values.SelectMany(v => v)).ToArray();

        /// <summary>
        /// Returns the players with given permission
        /// </summary>
        /// <returns></returns>
        [LibraryFunction("GetPermissionUsers")]
        public string[] GetPermissionUsers(string perm)
        {
            if (string.IsNullOrEmpty(perm))
            {
                return new string[0];
            }

            perm = perm.ToLower();
            HashSet<string> users = new HashSet<string>();
            foreach (KeyValuePair<string, UserData> data in userdata)
            {
                if (data.Value.Perms.Contains(perm))
                {
                    users.Add($"{data.Key}({data.Value.LastSeenNickname})");
                }
            }

            return users.ToArray();
        }

        /// <summary>
        /// Returns the groups with given permission
        /// </summary>
        /// <returns></returns>
        [LibraryFunction("GetPermissionGroups")]
        public string[] GetPermissionGroups(string perm)
        {
            if (string.IsNullOrEmpty(perm))
            {
                return new string[0];
            }

            perm = perm.ToLower();
            HashSet<string> groups = new HashSet<string>();
            foreach (KeyValuePair<string, GroupData> data in groupdata)
            {
                if (data.Value.Perms.Contains(perm))
                {
                    groups.Add(data.Key);
                }
            }

            return groups.ToArray();
        }

        /// <summary>
        /// Set the group to which the specified user belongs
        /// </summary>
        /// <param name="id"></param>
        /// <param name="name"></param>
        /// <returns></returns>
        [LibraryFunction("AddUserGroup")]
        public void AddUserGroup(string id, string name)
        {
            if (!GroupExists(name))
            {
                return;
            }

            UserData data = GetUserData(id);
            if (!data.Groups.Add(name.ToLower()))
            {
                return;
            }

            // Call hook for plugins
            Interface.Call("OnUserGroupAdded", id, name);
        }

        /// <summary>
        /// Set the group to which the specified user belongs
        /// </summary>
        /// <param name="id"></param>
        /// <param name="name"></param>
        /// <returns></returns>
        [LibraryFunction("RemoveUserGroup")]
        public void RemoveUserGroup(string id, string name)
        {
            if (!GroupExists(name))
            {
                return;
            }

            UserData data = GetUserData(id);
            if (name.Equals("*"))
            {
                if (data.Groups.Count <= 0)
                {
                    return;
                }

                data.Groups.Clear();
                return;
            }

            if (!data.Groups.Remove(name.ToLower()))
            {
                return;
            }

            // Call hook for plugins
            Interface.Call("OnUserGroupRemoved", id, name);
        }

        /// <summary>
        /// Get if the player belongs to given group
        /// </summary>
        /// <param name="id"></param>
        /// <param name="name"></param>
        /// <returns></returns>
        [LibraryFunction("UserHasGroup")]
        public bool UserHasGroup(string id, string name)
        {
            if (!GroupExists(name))
            {
                return false;
            }

            UserData data = GetUserData(id);
            return data.Groups.Contains(name.ToLower());
        }

        /// <summary>
        /// Returns if the specified group exists or not
        /// </summary>
        /// <param name="group"></param>
        /// <returns></returns>
        [LibraryFunction("GroupExists")]
        public bool GroupExists(string group)
        {
            return !string.IsNullOrEmpty(group) && (group.Equals("*") || groupdata.ContainsKey(group.ToLower()));
        }

        /// <summary>
        /// Returns existing groups
        /// </summary>
        /// <returns></returns>
        [LibraryFunction("GetGroups")]
        public string[] GetGroups() => groupdata.Keys.ToArray();

        /// <summary>
        /// Returns users in that group
        /// </summary>
        /// <param name="group"></param>
        /// <returns></returns>
        [LibraryFunction("GetUsersInGroup")]
        public string[] GetUsersInGroup(string group)
        {
            if (!GroupExists(group))
            {
                return new string[0];
            }

            group = group.ToLower();
            return userdata.Where(u => u.Value.Groups.Contains(group)).Select(u => $"{u.Key} ({u.Value.LastSeenNickname})").ToArray();
        }

        /// <summary>
        /// Returns the title of the specified group
        /// </summary>
        /// <param name="group"></param>
        [LibraryFunction("GetGroupTitle")]
        public string GetGroupTitle(string group)
        {
            if (!GroupExists(group))
            {
                return string.Empty;
            }

            // First, get the group data
            if (!groupdata.TryGetValue(group.ToLower(), out GroupData data))
            {
                return string.Empty;
            }

            // Return the group title
            return data.Title;
        }

        /// <summary>
        /// Returns the rank of the specified group
        /// </summary>
        /// <param name="group"></param>
        /// <returns></returns>
        [LibraryFunction("GetGroupRank")]
        public int GetGroupRank(string group)
        {
            if (!GroupExists(group))
            {
                return 0;
            }

            // First, get the group data
            if (!groupdata.TryGetValue(group.ToLower(), out GroupData data))
            {
                return 0;
            }

            // Return the group rank
            return data.Rank;
        }

        #endregion Querying

        #region User Permission

        /// <summary>
        /// Grants the specified permission to the specified user
        /// </summary>
        /// <param name="id"></param>
        /// <param name="perm"></param>
        /// <param name="owner"></param>
        [LibraryFunction("GrantUserPermission")]
        public void GrantUserPermission(string id, string perm, Plugin owner)
        {
            // Check it's even a perm
            if (!PermissionExists(perm, owner))
            {
                return;
            }

            // Get the player data
            UserData data = GetUserData(id);

            perm = perm.ToLower();

            if (perm.EndsWith("*"))
            {
                HashSet<string> perms;
                if (owner == null)
                {
                    perms = new HashSet<string>(permset.Values.SelectMany(v => v));
                }
                else if (!permset.TryGetValue(owner, out perms))
                {
                    return;
                }

                if (perm.Equals("*"))
                {
                    if (!perms.Aggregate(false, (c, s) => c | data.Perms.Add(s)))
                    {
                        return;
                    }
                }
                else
                {
                    perm = perm.TrimEnd('*');
                    if (!perms.Where(s => s.StartsWith(perm)).Aggregate(false, (c, s) => c | data.Perms.Add(s)))
                    {
                        return;
                    }
                }
                return;
            }

            // Add the permission
            if (!data.Perms.Add(perm))
            {
                return;
            }

            // Call hook for plugins
            Interface.Call("OnUserPermissionGranted", id, perm);
        }

        /// <summary>
        /// Revokes the specified permission from the specified user
        /// </summary>
        /// <param name="id"></param>
        /// <param name="perm"></param>
        [LibraryFunction("RevokeUserPermission")]
        public void RevokeUserPermission(string id, string perm)
        {
            if (string.IsNullOrEmpty(perm))
            {
                return;
            }

            // Get the player data
            UserData data = GetUserData(id);

            perm = perm.ToLower();

            if (perm.EndsWith("*"))
            {
                if (perm.Equals("*"))
                {
                    if (data.Perms.Count <= 0)
                    {
                        return;
                    }

                    data.Perms.Clear();
                }
                else
                {
                    perm = perm.TrimEnd('*');
                    if (data.Perms.RemoveWhere(s => s.StartsWith(perm)) <= 0)
                    {
                        return;
                    }
                }
                return;
            }

            // Remove the permission
            if (!data.Perms.Remove(perm))
            {
                return;
            }

            // Call hook for plugins
            Interface.Call("OnUserPermissionRevoked", id, perm);
        }

        #endregion User Permission

        #region Group Permission

        /// <summary>
        /// Grant the specified permission to the specified group
        /// </summary>
        /// <param name="name"></param>
        /// <param name="perm"></param>
        /// <param name="owner"></param>
        [LibraryFunction("GrantGroupPermission")]
        public void GrantGroupPermission(string name, string perm, Plugin owner)
        {
            // Check it's even a perm
            if (!PermissionExists(perm, owner) || !GroupExists(name))
            {
                return;
            }

            // Get the group data
            if (!groupdata.TryGetValue(name.ToLower(), out GroupData data))
            {
                return;
            }

            perm = perm.ToLower();

            if (perm.EndsWith("*"))
            {
                HashSet<string> perms;
                if (owner == null)
                {
                    perms = new HashSet<string>(permset.Values.SelectMany(v => v));
                }
                else if (!permset.TryGetValue(owner, out perms))
                {
                    return;
                }

                if (perm.Equals("*"))
                {
                    if (!perms.Aggregate(false, (c, s) => c | data.Perms.Add(s)))
                    {
                        return;
                    }
                }
                else
                {
                    perm = perm.TrimEnd('*').ToLower();
                    if (!perms.Where(s => s.StartsWith(perm)).Aggregate(false, (c, s) => c | data.Perms.Add(s)))
                    {
                        return;
                    }
                }
                return;
            }

            // Add the permission
            if (!data.Perms.Add(perm))
            {
                return;
            }

            // Call hook for plugins
            Interface.Call("OnGroupPermissionGranted", name, perm);
        }

        /// <summary>
        /// Revokes the specified permission from the specified user
        /// </summary>
        /// <param name="name"></param>
        /// <param name="perm"></param>
        [LibraryFunction("RevokeGroupPermission")]
        public void RevokeGroupPermission(string name, string perm)
        {
            if (!GroupExists(name) || string.IsNullOrEmpty(perm))
            {
                return;
            }

            // Get the group data
            if (!groupdata.TryGetValue(name.ToLower(), out GroupData data))
            {
                return;
            }

            perm = perm.ToLower();

            if (perm.EndsWith("*"))
            {
                if (perm.Equals("*"))
                {
                    if (data.Perms.Count <= 0)
                    {
                        return;
                    }

                    data.Perms.Clear();
                }
                else
                {
                    perm = perm.TrimEnd('*').ToLower();
                    if (data.Perms.RemoveWhere(s => s.StartsWith(perm)) <= 0)
                    {
                        return;
                    }
                }
                return;
            }

            // Remove the permission
            if (!data.Perms.Remove(perm))
            {
                return;
            }

            // Call hook for plugins
            Interface.Call("OnGroupPermissionRevoked", name, perm);
        }

        #endregion Group Permission

        #region Group Management

        /// <summary>
        /// Creates the specified group
        /// </summary>
        /// <param name="group"></param>
        /// <param name="title"></param>
        /// <param name="rank"></param>
        [LibraryFunction("CreateGroup")]
        public bool CreateGroup(string group, string title, int rank)
        {
            // Check if it already exists
            if (GroupExists(group) || string.IsNullOrEmpty(group))
            {
                return false;
            }

            // Create the data
            GroupData data = new GroupData { Title = title, Rank = rank };

            // Add the group
            group = group.ToLower();
            groupdata.Add(group, data);

            Interface.CallHook("OnGroupCreated", group, title, rank);

            return true;
        }

        /// <summary>
        /// Removes the specified group
        /// </summary>
        /// <param name="group"></param>
        [LibraryFunction("RemoveGroup")]
        public bool RemoveGroup(string group)
        {
            // Check if it even exists
            if (!GroupExists(group))
            {
                return false;
            }

            group = group.ToLower();

            // Remove the group
            bool removed = groupdata.Remove(group);

            // Remove group from users
            bool changed = userdata.Values.Aggregate(false, (current, userData) => current | userData.Groups.Remove(group));
            if (changed)
            {
                SaveUsers();
            }

            if (removed)
            {
                Interface.CallHook("OnGroupDeleted", group);
            }

            return true;
        }

        /// <summary>
        /// Sets the title of the specified group
        /// </summary>
        /// <param name="group"></param>
        /// <param name="title"></param>
        [LibraryFunction("SetGroupTitle")]
        public bool SetGroupTitle(string group, string title)
        {
            if (!GroupExists(group))
            {
                return false;
            }

            group = group.ToLower();

            // First, get the group data
            if (!groupdata.TryGetValue(group, out GroupData data))
            {
                return false;
            }

            // Change the title
            if (data.Title == title)
            {
                return true;
            }

            data.Title = title;

            Interface.CallHook("OnGroupTitleSet", group, title);

            return true;
        }

        /// <summary>
        /// Sets the rank of the specified group
        /// </summary>
        /// <param name="group"></param>
        /// <param name="rank"></param>
        [LibraryFunction("SetGroupRank")]
        public bool SetGroupRank(string group, int rank)
        {
            if (!GroupExists(group))
            {
                return false;
            }

            group = group.ToLower();

            // First, get the group data
            if (!groupdata.TryGetValue(group, out GroupData data))
            {
                return false;
            }

            // Change the rank
            if (data.Rank == rank)
            {
                return true;
            }

            data.Rank = rank;

            Interface.CallHook("OnGroupRankSet", group, rank);

            return true;
        }

        /// <summary>
        /// Gets the parent of the specified group
        /// </summary>
        /// <param name="group"></param>
        [LibraryFunction("GetGroupParent")]
        public string GetGroupParent(string group)
        {
            if (!GroupExists(group))
            {
                return string.Empty;
            }

            group = group.ToLower();

            return !groupdata.TryGetValue(group, out GroupData data) ? string.Empty : data.ParentGroup;
        }

        /// <summary>
        /// Sets the parent of the specified group
        /// </summary>
        /// <param name="group"></param>
        /// <param name="parent"></param>
        [LibraryFunction("SetGroupParent")]
        public bool SetGroupParent(string group, string parent)
        {
            if (!GroupExists(group))
            {
                return false;
            }

            group = group.ToLower();

            // First, get the group data
            if (!groupdata.TryGetValue(group, out GroupData data))
            {
                return false;
            }

            if (string.IsNullOrEmpty(parent))
            {
                data.ParentGroup = null;
                return true;
            }

            if (!GroupExists(parent) || group.Equals(parent.ToLower()))
            {
                return false;
            }

            parent = parent.ToLower();

            if (!string.IsNullOrEmpty(data.ParentGroup) && data.ParentGroup.Equals(parent))
            {
                return true;
            }

            if (HasCircularParent(group, parent))
            {
                return false;
            }

            // Change the parent group
            data.ParentGroup = parent;

            Interface.CallHook("OnGroupParentSet", group, parent);

            return true;
        }

        private bool HasCircularParent(string group, string parent)
        {
            // Get parent data

            if (!groupdata.TryGetValue(parent, out GroupData parentData))
            {
                return false;
            }

            // Check for circular reference
            HashSet<string> groups = new HashSet<string> { group, parent };
            while (!string.IsNullOrEmpty(parentData.ParentGroup))
            {
                // Found itself?
                if (!groups.Add(parentData.ParentGroup))
                {
                    return true;
                }

                // Get next parent
                if (!groupdata.TryGetValue(parentData.ParentGroup, out parentData))
                {
                    return false;
                }
            }
            return false;
        }

        #endregion Group Management
    }
}
