#if !WINDOWS
using SharpHook.Data;
using static Keysharp.Core.Common.Keyboard.KeyboardMouseSender;

namespace Keysharp.Core.Unix
{
	/// <summary>
	/// Tracks synthetic key events emitted by the sender and matches them against incoming hook events.
	/// Linux can optionally retain synthetic key-down tokens so auto-repeat remains synthetic while held.
	/// </summary>
	internal sealed class SyntheticEventQueue
	{
		private readonly object gate = new();
		private readonly List<SyntheticToken> pending = [];
		private readonly Dictionary<SyntheticKey, ReleasedDownGrace> recentlyReleasedDown = [];
		private readonly Dictionary<SyntheticKey, long> heldSyntheticDownOwners = [];

		private readonly struct SyntheticToken
		{
			internal readonly DateTimeOffset EnqueueTime;
			internal readonly KeyCode KeyCode;
			internal readonly uint Vk;
			internal readonly bool KeyUp;
			internal readonly long ExtraInfo;

			internal SyntheticToken(KeyCode keyCode, uint vk, bool keyUp, DateTimeOffset enqueueTime, long extraInfo)
			{
				KeyCode = keyCode;
				Vk = vk;
				KeyUp = keyUp;
				EnqueueTime = enqueueTime;
				ExtraInfo = extraInfo;
			}
		}

		private readonly struct SyntheticKey : IEquatable<SyntheticKey>
		{
			internal readonly KeyCode KeyCode;
			internal readonly uint Vk;

			internal SyntheticKey(KeyCode keyCode, uint vk)
			{
				KeyCode = keyCode;
				Vk = vk;
			}

			internal static SyntheticKey From(KeyCode keyCode, uint vk)
				=> new(keyCode, vk);

			public bool Equals(SyntheticKey other)
				=> Vk != 0 && other.Vk != 0
					? Vk == other.Vk
					: KeyCode == other.KeyCode && Vk == other.Vk;

			public override bool Equals(object obj)
				=> obj is SyntheticKey other && Equals(other);

			public override int GetHashCode()
				=> Vk != 0 ? Vk.GetHashCode() : HashCode.Combine((int)KeyCode, Vk);
		}

		private readonly struct ReleasedDownGrace
		{
			internal readonly DateTimeOffset ExpiresAt;
			internal readonly long ExtraInfo;

			internal ReleasedDownGrace(DateTimeOffset expiresAt, long extraInfo)
			{
				ExpiresAt = expiresAt;
				ExtraInfo = extraInfo;
			}
		}

		internal void Clear()
		{
			lock (gate)
			{
				pending.Clear();
				recentlyReleasedDown.Clear();
				heldSyntheticDownOwners.Clear();
			}
		}

		internal void Register(KeyCode keyCode, uint vk, bool keyUp, DateTime enqueueTime, long extraInfo, bool keepSyntheticDownTokensForRepeats)
		{
			if (keyCode == KeyCode.VcUndefined)
				return;

			lock (gate)
			{
				var key = SyntheticKey.From(keyCode, vk);
				var trackHeldOwner = keepSyntheticDownTokensForRepeats && extraInfo != KeyIgnore;

				if (trackHeldOwner)
				{
					if (!keyUp)
					{
						// Owner-wins: once a synthetic hold exists for this key, don't churn duplicate
						// synthetic down tokens from other producers while the owner is active.
						if (heldSyntheticDownOwners.ContainsKey(key))
							return;

						heldSyntheticDownOwners[key] = extraInfo;
					}
					else if (heldSyntheticDownOwners.TryGetValue(key, out var ownerExtraInfo))
					{
						// Non-owner key-up must not end a held synthetic ownership.
						if (ownerExtraInfo != extraInfo)
							return;

						// End ownership at synthetic key-up send time so later physical input
						// on this key is not misclassified while waiting for hook observation.
						_ = RemoveMatchingHeldDownTokenNoLock(keyCode, vk, out _);
					}
				}

				pending.Add(new SyntheticToken(keyCode, vk, keyUp, enqueueTime, extraInfo));
			}
		}

		internal bool TryGetHeldSyntheticDownOwnerExtra(KeyCode keyCode, uint vk, out ulong extraInfo)
		{
			extraInfo = 0;

			if (keyCode == KeyCode.VcUndefined)
				return false;

			lock (gate)
			{
				if (!heldSyntheticDownOwners.TryGetValue(SyntheticKey.From(keyCode, vk), out var ownerExtraInfo))
					return false;

				extraInfo = (ulong)ownerExtraInfo;
				return true;
			}
		}

		internal bool TryConsume(KeyCode keyCode, uint vk, bool keyUp, DateTimeOffset hookEventTime, long keyDownTimeoutMs, long keyUpTimeoutMs, long futureToleranceMs, bool keepSyntheticDownTokensForRepeats, out ulong extraInfo)
		{
			extraInfo = 0;

			if (keyCode == KeyCode.VcUndefined)
				return false;

			lock (gate)
			{
				CleanupReleasedDownGraceNoLock(hookEventTime);

				for (var i = 0; i < pending.Count; i++)
				{
					var token = pending[i];
					var keepDownForRepeats =
						keepSyntheticDownTokensForRepeats &&
						!token.KeyUp &&
						token.ExtraInfo != KeyIgnore;
					var ageMs = (hookEventTime - token.EnqueueTime).TotalMilliseconds;
					var timeoutMs = token.KeyUp ? keyUpTimeoutMs : keyDownTimeoutMs;

					if (ageMs > timeoutMs)
					{
						if (keepDownForRepeats)
						{
							if (!keyUp && TokenMatches(token, keyCode, vk, keyUp))
							{
								extraInfo = (ulong)token.ExtraInfo;
								return true;
							}

							continue;
						}

						if (token.KeyUp)
						{
							// If the matching synthetic key-up token expired before being observed,
							// always retire its retained key-down token as well. Otherwise a stale
							// synthetic "down" can leak and misclassify future physical key presses.
							pending.RemoveAt(i);
							i--;
							_ = RemoveMatchingHeldDownTokenNoLock(token.KeyCode, token.Vk, out _);
							continue;
						}

						pending.RemoveAt(i);
						i--;
						continue;
					}

					if (ageMs < 0)
					{
						if (TokenMatches(token, keyCode, vk, keyUp))
						{
							if (keepDownForRepeats)
							{
								if (!keyUp)
								{
									extraInfo = (ulong)token.ExtraInfo;
									return true;
								}

								if (keyUp && token.KeyUp)
								{
									extraInfo = (ulong)token.ExtraInfo;
									pending.RemoveAt(i);
									if (RemoveMatchingHeldDownTokenNoLock(keyCode, vk, out var releasedExtraInfo))
										RememberReleasedDownNoLock(keyCode, vk, releasedExtraInfo, hookEventTime, keyUpTimeoutMs);
									return true;
								}
							}
							else
							{
								extraInfo = (ulong)token.ExtraInfo;

								if (token.KeyUp)
								{
									pending.RemoveAt(i);
									if (RemoveMatchingHeldDownTokenNoLock(keyCode, vk, out var releasedExtraInfo))
										RememberReleasedDownNoLock(keyCode, vk, releasedExtraInfo, hookEventTime, keyUpTimeoutMs);
								}
								else
									pending.RemoveAt(i);

								return true;
							}
						}

						if (ageMs < -futureToleranceMs)
							break;

						continue;
					}

					if (keepDownForRepeats)
					{
						if (!keyUp && TokenMatches(token, keyCode, vk, keyUp))
						{
							extraInfo = (ulong)token.ExtraInfo;
							return true;
						}

						if (keyUp && token.KeyUp && TokenMatches(token, keyCode, vk, keyUp: true))
						{
							extraInfo = (ulong)token.ExtraInfo;
							pending.RemoveAt(i);
							if (RemoveMatchingHeldDownTokenNoLock(keyCode, vk, out var releasedExtraInfo))
								RememberReleasedDownNoLock(keyCode, vk, releasedExtraInfo, hookEventTime, keyUpTimeoutMs);
							return true;
						}

						continue;
					}

					if (!TokenMatches(token, keyCode, vk, keyUp))
						continue;

					extraInfo = (ulong)token.ExtraInfo;

					if (token.KeyUp)
					{
						pending.RemoveAt(i);
						if (RemoveMatchingHeldDownTokenNoLock(keyCode, vk, out var releasedExtraInfo))
							RememberReleasedDownNoLock(keyCode, vk, releasedExtraInfo, hookEventTime, keyUpTimeoutMs);
						return true;
					}

					pending.RemoveAt(i);
					return true;
				}

				if (!keyUp
					&& !keepSyntheticDownTokensForRepeats
					&& TryConsumeReleasedDownNoLock(keyCode, vk, hookEventTime, out extraInfo))
					return true;
			}

			return false;
		}

		internal bool TryConsumeByKey(KeyCode keyCode, uint vk, bool keyUp, bool keepSyntheticDownTokensForRepeats, out ulong extraInfo)
		{
			extraInfo = 0;

			if (keyCode == KeyCode.VcUndefined)
				return false;

			lock (gate)
			{
				CleanupReleasedDownGraceNoLock(DateTimeOffset.UtcNow);

				for (var i = 0; i < pending.Count; i++)
				{
					var token = pending[i];

					if (!TokenMatches(token, keyCode, vk, keyUp))
						continue;

					extraInfo = (ulong)token.ExtraInfo;
					var keepDownForRepeats =
						keepSyntheticDownTokensForRepeats &&
						!token.KeyUp &&
						token.ExtraInfo != KeyIgnore;

					if (token.KeyUp)
					{
						pending.RemoveAt(i);
						if (RemoveMatchingHeldDownTokenNoLock(keyCode, vk, out var releasedExtraInfo))
							RememberReleasedDownNoLock(keyCode, vk, releasedExtraInfo, DateTimeOffset.UtcNow, 200);
						return true;
					}

					if (!keepDownForRepeats)
						pending.RemoveAt(i);

					return true;
				}

				if (!keyUp
					&& !keepSyntheticDownTokensForRepeats
					&& TryConsumeReleasedDownNoLock(keyCode, vk, DateTimeOffset.UtcNow, out extraInfo))
					return true;
			}

			return false;
		}

		private bool RemoveMatchingHeldDownTokenNoLock(KeyCode keyCode, uint vk, out long releasedExtraInfo)
		{
			releasedExtraInfo = 0;
			var removedAny = false;

			for (var i = pending.Count - 1; i >= 0; i--)
			{
				var candidate = pending[i];

				if (candidate.KeyUp)
					continue;

				if (!TokenMatches(candidate, keyCode, vk, keyUp: false))
					continue;

				if (!removedAny)
				{
					removedAny = true;
					releasedExtraInfo = candidate.ExtraInfo;
				}

				pending.RemoveAt(i);
			}

			if (removedAny)
			{
				heldSyntheticDownOwners.Remove(SyntheticKey.From(keyCode, vk));
			}

			return removedAny;
		}

		private void RememberReleasedDownNoLock(KeyCode keyCode, uint vk, long extraInfo, DateTimeOffset eventTime, long keyUpTimeoutMs)
		{
			if (extraInfo == KeyIgnore)
				return;

			var graceMs = Math.Max(10, Math.Min(60, keyUpTimeoutMs / 4));
			recentlyReleasedDown[SyntheticKey.From(keyCode, vk)] = new ReleasedDownGrace(eventTime.AddMilliseconds(graceMs), extraInfo);
		}

		private bool TryConsumeReleasedDownNoLock(KeyCode keyCode, uint vk, DateTimeOffset now, out ulong extraInfo)
		{
			extraInfo = 0;
			var key = SyntheticKey.From(keyCode, vk);

			if (!recentlyReleasedDown.TryGetValue(key, out var grace))
				return false;

			if (grace.ExpiresAt < now)
			{
				recentlyReleasedDown.Remove(key);
				return false;
			}

			extraInfo = (ulong)grace.ExtraInfo;
			return true;
		}

		private void CleanupReleasedDownGraceNoLock(DateTimeOffset now)
		{
			if (recentlyReleasedDown.Count == 0)
				return;

			var expired = new List<SyntheticKey>();

			foreach (var kv in recentlyReleasedDown)
			{
				if (kv.Value.ExpiresAt < now)
					expired.Add(kv.Key);
			}

			for (var i = 0; i < expired.Count; i++)
				recentlyReleasedDown.Remove(expired[i]);
		}

		private static bool TokenMatches(SyntheticToken token, KeyCode keyCode, uint vk, bool keyUp)
		{
			if (token.KeyUp != keyUp)
				return false;

			if (vk != 0 && token.Vk != 0 && token.Vk == vk)
				return true;

			return token.KeyCode == keyCode;
		}

	}
}
#endif
