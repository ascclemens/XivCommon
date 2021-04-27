﻿using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace XivCommon.Functions {
    /// <summary>
    /// Context menu functions
    /// </summary>
    public class ContextMenu : IDisposable {
        private static class Signatures {
            internal const string ContextMenuOpen = "48 8B C4 57 41 56 41 57 48 81 EC ?? ?? ?? ??";
            internal const string ContextMenuSelected = "48 89 5C 24 ?? 55 57 41 56 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 ?? 80 B9 ?? ?? ?? ?? ??";
            internal const string AtkValueChangeType = "E8 ?? ?? ?? ?? 45 84 F6 48 8D 4C 24 ??";
            internal const string AtkValueSetString = "E8 ?? ?? ?? ?? 41 03 ED";
            internal const string GetAddonByInternalId = "E8 ?? ?? ?? ?? 8B 6B 20";
        }

        /// <summary>
        /// Offset from addon to menu type
        /// </summary>
        private const int ParentAddonIdOffset = 0x1D2;

        /// <summary>
        /// Offset from agent to actions byte array pointer (have to add the actions offset after)
        /// </summary>
        private const int MenuActionsPointerOffset = 0xD18;

        /// <summary>
        /// Offset from agent to actions byte array
        /// </summary>
        private const int MenuActionsOffset = 0x428;

        private const int ActorIdOffset = 0xEF0;
        private const int ContentIdLowerOffset = 0xEE0;

        private const int TextPointerOffset = 0xE08;
        private const int WorldOffset = 0xF00;

        private const int NoopContextId = 0x67;

        /// <summary>
        /// The delegate for context menu events.
        /// </summary>
        public delegate void ContextMenuEventDelegate(ContextMenuArgs args);

        public event ContextMenuEventDelegate? OpenContextMenu;

        /// <summary>
        /// The delegate that is run when a context menu item is selected.
        /// </summary>
        public delegate void ContextMenuItemSelectedDelegate(ContextMenuItemSelectedArgs args);

        private unsafe delegate byte ContextMenuOpenDelegate(IntPtr addon, int menuSize, AtkValue* atkValueArgs);

        private delegate IntPtr GetAddonByInternalIdDelegate(IntPtr raptureAtkUnitManager, short id);

        private readonly GetAddonByInternalIdDelegate _getAddonByInternalId = null!;

        private Hook<ContextMenuOpenDelegate>? ContextMenuOpenHook { get; }

        private delegate byte ContextMenuItemSelectedInternalDelegate(IntPtr addon, int index, byte a3);

        private Hook<ContextMenuItemSelectedInternalDelegate>? ContextMenuItemSelectedHook { get; }

        private unsafe delegate void AtkValueChangeTypeDelegate(AtkValue* thisPtr, ValueType type);

        private readonly AtkValueChangeTypeDelegate _atkValueChangeType = null!;

        private unsafe delegate void AtkValueSetStringDelegate(AtkValue* thisPtr, byte* bytes);

        private readonly AtkValueSetStringDelegate _atkValueSetString = null!;

        private GameFunctions Functions { get; }
        private ClientLanguage Language { get; }
        private List<ContextMenuItem> Items { get; } = new();
        private int NormalSize { get; set; }

        internal ContextMenu(GameFunctions functions, SigScanner scanner, ClientLanguage language, Hooks hooks) {
            this.Functions = functions;
            this.Language = language;

            if (!hooks.HasFlag(Hooks.ContextMenu)) {
                return;
            }

            if (scanner.TryScanText(Signatures.AtkValueChangeType, out var changeTypePtr, "Context Menu (change type)")) {
                this._atkValueChangeType = Marshal.GetDelegateForFunctionPointer<AtkValueChangeTypeDelegate>(changeTypePtr);
            } else {
                return;
            }

            if (scanner.TryScanText(Signatures.AtkValueSetString, out var setStringPtr, "Context Menu (set string)")) {
                this._atkValueSetString = Marshal.GetDelegateForFunctionPointer<AtkValueSetStringDelegate>(setStringPtr);
            } else {
                return;
            }

            if (scanner.TryScanText(Signatures.GetAddonByInternalId, out var getAddonPtr, "Context Menu (get addon)")) {
                this._getAddonByInternalId = Marshal.GetDelegateForFunctionPointer<GetAddonByInternalIdDelegate>(getAddonPtr);
            } else {
                return;
            }

            if (scanner.TryScanText(Signatures.ContextMenuOpen, out var openPtr, "Context Menu open")) {
                unsafe {
                    this.ContextMenuOpenHook = new Hook<ContextMenuOpenDelegate>(openPtr, new ContextMenuOpenDelegate(this.OpenMenuDetour));
                }

                this.ContextMenuOpenHook.Enable();
            } else {
                return;
            }

            if (scanner.TryScanText(Signatures.ContextMenuSelected, out var selectedPtr, "Context Menu selected")) {
                this.ContextMenuItemSelectedHook = new Hook<ContextMenuItemSelectedInternalDelegate>(selectedPtr, new ContextMenuItemSelectedInternalDelegate(this.ItemSelectedDetour));
                this.ContextMenuItemSelectedHook.Enable();
            }
        }

        /// <inheritdoc />
        public void Dispose() {
            this.ContextMenuOpenHook?.Dispose();
            this.ContextMenuItemSelectedHook?.Dispose();
        }

        private IntPtr GetContextMenuAgent() {
            return this.Functions.GetAgentByInternalId(9);
        }

        private unsafe string? GetParentAddonName(IntPtr addon) {
            var parentAddonId = Marshal.ReadInt16(addon + ParentAddonIdOffset);
            if (parentAddonId == 0) {
                return null;
            }

            var stage = (AtkStage*) this.Functions.GetAtkStageSingleton();
            var parentAddon = this._getAddonByInternalId((IntPtr) stage->RaptureAtkUnitManager, parentAddonId);
            return Encoding.UTF8.GetString(Util.ReadTerminated(parentAddon + 8));
        }

        private static unsafe (uint actorId, uint contentIdLower, string? text, ushort actorWorld) GetAgentInfo(IntPtr agent) {
            var actorId = *(uint*) (agent + ActorIdOffset);
            var contentIdLower = *(uint*) (agent + ContentIdLowerOffset);
            var textBytes = Util.ReadTerminated(Marshal.ReadIntPtr(agent + TextPointerOffset));
            var text = textBytes.Length == 0 ? null : Encoding.UTF8.GetString(textBytes);
            var actorWorld = *(ushort*) (agent + WorldOffset);
            return (actorId, contentIdLower, text, actorWorld);
        }

        private unsafe byte OpenMenuDetour(IntPtr addon, int menuSize, AtkValue* atkValueArgs) {
            this.NormalSize = (int) (&atkValueArgs[0])->UInt;

            var addonName = this.GetParentAddonName(addon);

            var agent = this.GetContextMenuAgent();
            var info = GetAgentInfo(agent);
            this.Items.Clear();

            var args = new ContextMenuArgs(
                addon,
                agent,
                addonName,
                info.actorId,
                info.contentIdLower,
                info.text,
                info.actorWorld
            );
            try {
                this.OpenContextMenu?.Invoke(args);
            } catch (Exception ex) {
                PluginLog.LogError(ex, "Exception in OpenMenuDetour");
                goto Original;
            }

            this.Items.AddRange(args.AdditionalItems);

            for (var i = 0; i < this.Items.Count; i++) {
                var item = this.Items[i];

                // set up the agent to ignore this item
                var menuActions = (byte*) (Marshal.ReadIntPtr(agent + MenuActionsPointerOffset) + MenuActionsOffset);
                *(menuActions + 7 + this.NormalSize + i) = NoopContextId;

                // set up the new menu item
                var newItem = &atkValueArgs[7 + this.NormalSize + i];
                this._atkValueChangeType(newItem, ValueType.String);
                var name = this.Language switch {
                    ClientLanguage.Japanese => item.NameJapanese,
                    ClientLanguage.English => item.NameEnglish,
                    ClientLanguage.German => item.NameGerman,
                    ClientLanguage.French => item.NameFrench,
                    _ => throw new ArgumentOutOfRangeException(),
                };
                var nameBytes = Encoding.UTF8.GetBytes(name).Terminate();
                fixed (byte* nameBytesPtr = nameBytes) {
                    this._atkValueSetString(newItem, nameBytesPtr);
                }

                // increment the menu size
                (&atkValueArgs[0])->UInt += 1;
            }

            menuSize = 7 + (int) (&atkValueArgs[0])->UInt;

            Original:
            return this.ContextMenuOpenHook!.Original(addon, menuSize, atkValueArgs);
        }

        private byte ItemSelectedDetour(IntPtr addon, int index, byte a3) {
            var addonName = this.GetParentAddonName(addon);

            // a custom item is being clicked
            if (index >= this.NormalSize) {
                var idx = index - this.NormalSize;
                if (this.Items.Count <= idx) {
                    goto Original;
                }

                var agent = this.GetContextMenuAgent();
                var info = GetAgentInfo(agent);

                var item = this.Items[idx];
                try {
                    item.Action(new ContextMenuItemSelectedArgs(
                        addon,
                        agent,
                        addonName,
                        info.actorId,
                        info.contentIdLower,
                        info.text,
                        info.actorWorld
                    ));
                } catch (Exception ex) {
                    PluginLog.LogError(ex, "Exception in custom context menu item");
                }
            }

            Original:
            return this.ContextMenuItemSelectedHook!.Original(addon, index, a3);
        }
    }

    /// <summary>
    /// A custom context menu item
    /// </summary>
    public class ContextMenuItem {
        /// <summary>
        /// A unique ID to identify this item.
        /// </summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>
        /// The name of the context item to be shown for English clients.
        /// </summary>
        public string NameEnglish { get; set; }

        /// <summary>
        /// The name of the context item to be shown for Japanese clients.
        /// </summary>
        public string NameJapanese { get; set; }

        /// <summary>
        /// The name of the context item to be shown for French clients.
        /// </summary>
        public string NameFrench { get; set; }

        /// <summary>
        /// The name of the context item to be shown for German clients.
        /// </summary>
        public string NameGerman { get; set; }

        /// <summary>
        /// The action to perform when this item is clicked.
        /// </summary>
        public ContextMenu.ContextMenuItemSelectedDelegate Action { get; set; }

        /// <summary>
        /// Create a new context menu item.
        /// </summary>
        /// <param name="name">the English name of the item, copied to other languages</param>
        /// <param name="action">the action to perform on click</param>
        public ContextMenuItem(string name, ContextMenu.ContextMenuItemSelectedDelegate action) {
            this.NameEnglish = name;
            this.NameJapanese = name;
            this.NameFrench = name;
            this.NameGerman = name;

            this.Action = action;
        }
    }

    /// <summary>
    /// Arguments for the context menu item selected delegate.
    /// </summary>
    public class ContextMenuItemSelectedArgs {
        /// <summary>
        /// Pointer to the context menu addon.
        /// </summary>
        public IntPtr Addon { get; }

        /// <summary>
        /// Pointer to the context menu agent.
        /// </summary>
        public IntPtr Agent { get; }

        /// <summary>
        /// The name of the addon containing this context menu, if any.
        /// </summary>
        public string? ParentAddonName { get; }

        /// <summary>
        /// The actor ID for this context menu. May be invalid (0xE0000000).
        /// </summary>
        public uint ActorId { get; }

        /// <summary>
        /// The lower half of the content ID of the actor for this context menu. May be zero.
        /// </summary>
        public uint ContentIdLower { get; }

        /// <summary>
        /// The text related to this context menu, usually an actor name.
        /// </summary>
        public string? Text { get; }

        /// <summary>
        /// The world of the actor this context menu is for, if any.
        /// </summary>
        public ushort ActorWorld { get; }

        internal ContextMenuItemSelectedArgs(IntPtr addon, IntPtr agent, string? parentAddonName, uint actorId, uint contentIdLower, string? text, ushort actorWorld) {
            this.Addon = addon;
            this.Agent = agent;
            this.ParentAddonName = parentAddonName;
            this.ContentIdLower = contentIdLower;
            this.ActorId = actorId;
            this.Text = text;
            this.ActorWorld = actorWorld;
        }
    }

    /// <summary>
    /// Arguments for the context menu event.
    /// </summary>
    public class ContextMenuArgs {
        /// <summary>
        /// Pointer to the context menu addon.
        /// </summary>
        public IntPtr Addon { get; }

        /// <summary>
        /// Pointer to the context menu agent.
        /// </summary>
        public IntPtr Agent { get; }

        /// <summary>
        /// The name of the addon containing this context menu, if any.
        /// </summary>
        public string? ParentAddonName { get; }

        /// <summary>
        /// The actor ID for this context menu. May be invalid (0xE0000000).
        /// </summary>
        public uint ActorId { get; }

        /// <summary>
        /// The lower half of the content ID of the actor for this context menu. May be zero.
        /// </summary>
        public uint ContentIdLower { get; }

        /// <summary>
        /// The text related to this context menu, usually an actor name.
        /// </summary>
        public string? Text { get; }

        /// <summary>
        /// The world of the actor this context menu is for, if any.
        /// </summary>
        public ushort ActorWorld { get; }

        /// <summary>
        /// Additional context menu items to add to this menu.
        /// </summary>
        public List<ContextMenuItem> AdditionalItems { get; } = new();

        internal ContextMenuArgs(IntPtr addon, IntPtr agent, string? parentAddonName, uint actorId, uint contentIdLower, string? text, ushort actorWorld) {
            this.Addon = addon;
            this.Agent = agent;
            this.ParentAddonName = parentAddonName;
            this.ActorId = actorId;
            this.ContentIdLower = contentIdLower;
            this.Text = text;
            this.ActorWorld = actorWorld;
        }
    }
}
