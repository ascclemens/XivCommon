﻿using System;
using Dalamud.Game;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Plugin;

namespace XivCommon.Functions {
    public class BattleTalk : IDisposable {
        private GameFunctions Functions { get; }
        private SeStringManager SeStringManager { get; }
        private bool Enabled { get; }

        public delegate void BattleTalkEventDelegate(ref SeString sender, ref SeString message, ref BattleTalkOptions options, ref bool isHandled);

        /// <summary>
        /// The event that is fired when a BattleTalk window is shown.
        /// </summary>
        public event BattleTalkEventDelegate? OnBattleTalk;

        private delegate byte AddBattleTalkDelegate(IntPtr uiModule, IntPtr sender, IntPtr message, float duration, byte style);

        private Hook<AddBattleTalkDelegate>? AddBattleTextHook { get; }

        internal BattleTalk(GameFunctions functions, SigScanner scanner, SeStringManager seStringManager, bool hook) {
            this.Functions = functions;
            this.SeStringManager = seStringManager;
            this.Enabled = hook;

            if (!hook) {
                return;
            }

            var addBattleTextPtr = scanner.ScanText("48 89 5C 24 ?? 57 48 83 EC 50 48 8B 01 49 8B D8 0F 29 74 24 ?? 48 8B FA 0F 28 F3 FF 50 40 C7 44 24 ?? ?? ?? ?? ??");
            this.AddBattleTextHook = new Hook<AddBattleTalkDelegate>(addBattleTextPtr, new AddBattleTalkDelegate(this.AddBattleTalkDetour));
            this.AddBattleTextHook.Enable();
        }

        public void Dispose() {
            this.AddBattleTextHook?.Dispose();
        }

        private unsafe byte AddBattleTalkDetour(IntPtr uiModule, IntPtr senderPtr, IntPtr messagePtr, float duration, byte style) {
            var rawSender = Util.ReadTerminated(senderPtr);
            var rawMessage = Util.ReadTerminated(messagePtr);

            var sender = this.SeStringManager.Parse(rawSender);
            var message = this.SeStringManager.Parse(rawMessage);

            var options = new BattleTalkOptions {
                Duration = duration,
                Style = (BattleTalkStyle) style,
            };

            var handled = false;
            try {
                this.OnBattleTalk?.Invoke(ref sender, ref message, ref options, ref handled);
            } catch (Exception ex) {
                PluginLog.Log(ex, "Exception in BattleTalk detour");
            }

            if (handled) {
                return 0;
            }

            var finalSender = sender.Encode().Terminate();
            var finalMessage = message.Encode().Terminate();

            fixed (byte* fSenderPtr = finalSender, fMessagePtr = finalMessage) {
                return this.AddBattleTextHook!.Original(uiModule, (IntPtr) fSenderPtr, (IntPtr) fMessagePtr, options.Duration, (byte) options.Style);
            }
        }

        /// <summary>
        /// Show a BattleTalk window with the given options.
        /// </summary>
        /// <param name="sender">The name to attribute to the message</param>
        /// <param name="message">The message to show in the window</param>
        /// <param name="options">Optional options for the window</param>
        /// <exception cref="InvalidOperationException">If the <see cref="Hooks.BattleTalk"/> hook is not enabled</exception>
        public void Show(SeString sender, SeString message, BattleTalkOptions? options = null) {
            this.Show(sender.Encode(), message.Encode(), options);
        }

        private void Show(byte[] sender, byte[] message, BattleTalkOptions? options) {
            if (!this.Enabled) {
                throw new InvalidOperationException("BattleTalk hooks are not enabled");
            }

            if (sender.Length == 0) {
                throw new ArgumentException("sender cannot be empty", nameof(sender));
            }

            if (message.Length == 0) {
                throw new ArgumentException("message cannot be empty", nameof(message));
            }

            options ??= new BattleTalkOptions();

            var uiModule = this.Functions.GetUiModule();

            unsafe {
                fixed (byte* senderPtr = sender.Terminate(), messagePtr = message.Terminate()) {
                    this.AddBattleTalkDetour(uiModule, (IntPtr) senderPtr, (IntPtr) messagePtr, options.Duration, (byte) options.Style);
                }
            }
        }
    }

    public class BattleTalkOptions {
        /// <summary>
        /// Duration to display the window, in seconds.
        /// </summary>
        public float Duration { get; set; } = 5f;

        /// <summary>
        /// The style of the window.
        /// </summary>
        public BattleTalkStyle Style { get; set; } = BattleTalkStyle.Normal;
    }

    public enum BattleTalkStyle : byte {
        /// <summary>
        /// A normal battle talk window with a white background.
        /// </summary>
        Normal = 0,

        /// <summary>
        /// A battle talk window with a blue background and styled edges.
        /// </summary>
        Aetherial = 6,

        /// <summary>
        /// A battle talk window styled similarly to a system text message (black background).
        /// </summary>
        System = 7,

        /// <summary>
        /// <para>
        /// A battle talk window with a blue, computer-y background.
        /// </para>
        /// <para>
        /// Used by the Ultima Weapons (Ruby, Emerald, etc.).
        /// </para>
        /// </summary>
        Blue = 9,
    }
}