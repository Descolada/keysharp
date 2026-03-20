#if LINUX
using System;
using System.Collections.Generic;
using Keysharp.Core.Common.Keyboard;
using static Keysharp.Core.Common.Keyboard.KeyboardMouseSender;

namespace Keysharp.Core.Unix
{
	/// <summary>
	/// Linux/X11-specific input state which can't be represented by the shared Unix hook alone.
	/// Owns hotstring-trigger suppression and the app-visible logical key state repairs needed around X11 sends.
	/// </summary>
	internal sealed class LinuxX11InputState
	{
		private readonly byte[] physicalKeyState;
		private readonly byte[] logicalKeyState;
		private uint pendingHotstringTriggerVk;

		internal LinuxX11InputState(byte[] physicalKeyState, byte[] logicalKeyState)
		{
			this.physicalKeyState = physicalKeyState;
			this.logicalKeyState = logicalKeyState;
		}

		internal void Reset()
		{
			System.Array.Clear(physicalKeyState, 0, physicalKeyState.Length);
			System.Array.Clear(logicalKeyState, 0, logicalKeyState.Length);
			pendingHotstringTriggerVk = 0;
		}

		internal void RecordInitialPhysicalKeyDown(uint vk)
		{
			if (vk == 0 || vk >= physicalKeyState.Length)
				return;

			physicalKeyState[vk] = StateDown;
			logicalKeyState[vk] = StateDown;
		}

		internal void RecordInitialLogicalKeyDown(uint vk)
		{
			if (vk == 0 || vk >= logicalKeyState.Length)
				return;

			logicalKeyState[vk] = StateDown;
		}

		internal bool ShouldSuppressPendingHotstringKeyDown(uint vk) => vk != 0 && vk == pendingHotstringTriggerVk;

		internal bool TrySuppressPendingHotstringKeyUp(uint vk)
		{
			if (vk == 0 || vk != pendingHotstringTriggerVk)
				return false;

			pendingHotstringTriggerVk = 0;
			ApplySyntheticKeyEvent(vk, isPress: false, updatePhysical: true);
			return true;
		}

		internal void TrackPendingHotstringTrigger(uint triggerVk)
		{
			pendingHotstringTriggerVk = triggerVk;
		}

		internal bool ShouldRestoreReleasedKey(uint vk, bool hotkeySuffixDown)
		{
			return vk != 0
				&& vk < physicalKeyState.Length
				&& (physicalKeyState[vk] & StateDown) != 0
				&& !hotkeySuffixDown
				&& vk != pendingHotstringTriggerVk;
		}

		internal void ApplySyntheticKeyEvent(uint vk, bool isPress, bool updatePhysical = false)
		{
			SetLogicalKeyState(vk, isPress);

			if (updatePhysical)
				SetPhysicalKeyState(vk, isPress);
		}

		internal void AddPredictedHotstringCompletionEnds(HotstringManager hm, ReadOnlySpan<char> hsBuf, HashSet<char> ends, Func<char, bool> isHotstringWordChar)
		{
			foreach (var hotstring in hm.shs)
			{
				if (TryGetImmediateHotstringCompletionChar(hotstring, hsBuf, isHotstringWordChar, out var completionChar))
					ends.Add(completionChar);
			}
		}

		private void SetLogicalKeyState(uint vk, bool isDown)
		{
			if (vk == 0 || vk >= logicalKeyState.Length)
				return;

			logicalKeyState[vk] = (byte)(isDown ? StateDown : 0);
		}

		private void SetPhysicalKeyState(uint vk, bool isDown)
		{
			if (vk == 0 || vk >= physicalKeyState.Length)
				return;

			physicalKeyState[vk] = (byte)(isDown ? StateDown : 0);
		}

		private static bool TryGetImmediateHotstringCompletionChar(HotstringDefinition hs, ReadOnlySpan<char> buffer, Func<char, bool> isHotstringWordChar, out char completionChar)
		{
			completionChar = '\0';

			if (hs == null || hs.suspended != 0 || hs.endCharRequired || string.IsNullOrEmpty(hs.str))
				return false;

			var prefixLength = hs.str.Length - 1;
			var start = buffer.Length - prefixLength;

			if (start < 0)
				return false;

			if (prefixLength > 0)
			{
				var prefix = hs.str.AsSpan(0, prefixLength);
				var candidate = buffer.Slice(start, prefixLength);

				if (hs.caseSensitive)
				{
					if (!candidate.SequenceEqual(prefix))
						return false;
				}
				else if (!candidate.Equals(prefix, StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
			}

			if (!hs.detectWhenInsideWord && start > 0 && isHotstringWordChar(buffer[start - 1]))
				return false;

			if (HotkeyDefinition.HotCriterionAllowsFiring(hs.hotCriterion, hs.Name) == 0L)
				return false;

			completionChar = hs.str[^1];
			return true;
		}
	}
}
#endif
