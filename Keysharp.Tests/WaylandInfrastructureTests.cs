#if LINUX
using System.Runtime.InteropServices;
using Keysharp.Internals;
using Keysharp.Internals.Window.Linux.Wayland;

namespace Keysharp.Tests
{
	[TestFixture]
	public class WaylandInfrastructureTests
	{
		[Test]
		public void WholeOverlayMoveWithinOutputUsesCachedTarget()
		{
			var output = new WaylandLayerShellClient.OutputTarget(4, new nint(9),
				new ScreenRect(-100, 0, 200, 100), 1.25, 1);
			var current = new WaylandLayerShellClient.OutputSegment(output,
				new ScreenRect(-80, 10, 20, 20), 0, 0);

			Assert.That(LayerImageBacking.TryResolveSameOutputMove(current, 20, 20,
				new ScreenRect(60, 30, 20, 20), out var moved), Is.True);
			Assert.That(moved.Output, Is.EqualTo(output));
			Assert.That(moved.Bounds, Is.EqualTo(new ScreenRect(60, 30, 20, 20)));
			Assert.That(LayerImageBacking.TryResolveSameOutputMove(current, 20, 20,
				new ScreenRect(90, 30, 20, 20), out _), Is.False,
				"crossing an output edge must take the topology/repaint path");
		}

		[Test]
		public void OutputGeometryUsesLogicalMetadataAndTransformFallback()
		{
			var output = new WaylandOutput
			{
				GeometryX = -1080,
				GeometryY = 0,
				Transform = 1,
				ModeWidth = 1920,
				ModeHeight = 1080,
				IntegerScale = 2
			};

			Assert.That(output.Bounds, Is.EqualTo(new ScreenRect(-1080, 0, 540, 960)));
			output.LogicalX = -900;
			output.LogicalY = 20;
			output.LogicalWidth = 720;
			output.LogicalHeight = 1280;
			output.HasLogicalPosition = output.HasLogicalSize = true;
			Assert.That(output.Bounds, Is.EqualTo(new ScreenRect(-900, 20, 720, 1280)));
		}

		[Test]
		public void PassiveCompositorBackingRejectsInteractiveMode()
		{
			using var backing = new CompositorImageBacking(42);
			Assert.That(backing.Show(null, new ScreenRect(0, 0, 1, 1), clickThrough: false), Is.False);
			Assert.That(WaylandImageOverlay.ResolveInputRegion(clickThrough: true, new nint(17)), Is.EqualTo(new nint(17)));
			Assert.That(WaylandImageOverlay.ResolveInputRegion(clickThrough: false, new nint(17)), Is.EqualTo(nint.Zero));
		}

		[Test]
		public void ShellOverlayAttemptDoesNotDependOnTransientOwnerProbe()
		{
			IWaylandBackend backend = new TransientProbeOverlayBackend();
			Assert.That(backend.SupportsImageOverlay, Is.False,
				"the cached availability probe is allowed to miss");
			Assert.That(LinuxImageOverlayBacking.ShouldAttemptCompositor(backend), Is.True,
				"a shell backend must still issue the authoritative Show call");
		}

		[Test]
		public void WaylandBridgeDiagnosticsThrottleRepeatedFailures()
		{
			var throttle = new WaylandDiagnosticThrottle(5000);
			Assert.That(throttle.TryAcquire("GNOME:NameHasOwner", 1000, out var suppressed), Is.True);
			Assert.That(suppressed, Is.Zero);
			Assert.That(throttle.TryAcquire("GNOME:NameHasOwner", 2000, out _), Is.False);
			Assert.That(throttle.TryAcquire("GNOME:ShowImageOverlay", 2000, out _), Is.True,
				"different operations must not suppress each other");
			Assert.That(throttle.TryAcquire("GNOME:NameHasOwner", 6000, out suppressed), Is.True);
			Assert.That(suppressed, Is.EqualTo(1));
		}

		[Test]
		public void ShmBufferPoolDropsFramesAtItsHardLimit()
		{
			WaylandBufferState[] buffers =
			[
				new(100, 100, false),
				new(100, 100, true),
				new(200, 100, true)
			];

			Assert.That(WaylandBufferPoolPolicy.FindReusable(buffers, 100, 100), Is.EqualTo(1));
			Assert.That(WaylandBufferPoolPolicy.FindReusable(buffers, 300, 100), Is.EqualTo(-1));
			Assert.That(WaylandBufferPoolPolicy.CanAllocate(2), Is.True);
			Assert.That(WaylandBufferPoolPolicy.CanAllocate(WaylandBufferPoolPolicy.Capacity), Is.False,
				"three in-flight, wrong-sized buffers must drop the new frame instead of growing the pool");
		}

		[Test]
		public void NearestSamplingUsesIntegerPixelCenters()
		{
			Assert.That(Enumerable.Range(0, 5).Select(x => WaylandImageOverlay.SampleIndex(x, 5, 3)),
				Is.EqualTo(new[] { 0, 0, 1, 2, 2 }));
			Assert.That(Enumerable.Range(0, 3).Select(x => WaylandImageOverlay.SampleIndex(x, 3, 5)),
				Is.EqualTo(new[] { 0, 2, 4 }));
		}

		[Test]
		public void OutputAbandonClearsHotplugIdentityAndListenerHandle()
		{
			var output = new WaylandOutput { RegistryName = 7 };
			output.Handle = GCHandle.Alloc(output);
			WaylandOutputBinding.Abandon(output);

			Assert.That(output.Proxy, Is.EqualTo(nint.Zero));
			Assert.That(output.XdgProxy, Is.EqualTo(nint.Zero));
			Assert.That(output.Handle.IsAllocated, Is.False);
		}

		[Test]
		public void ScreencopySessionIsRetiredOnlyWhenOperationMarksItUnusable()
		{
			FakeSession current = new();
			var first = current;
			Assert.That(WaylandScreenCapture.RunWithReusableSession(ref current, _ => (new object(), true)), Is.Not.Null);
			Assert.That(current, Is.SameAs(first));

			Assert.That(WaylandScreenCapture.RunWithReusableSession<FakeSession, object>(ref current,
				_ => (null, true)), Is.Null);
			Assert.That(first.Disposed, Is.False);
			Assert.That(current, Is.SameAs(first));

			Assert.That(WaylandScreenCapture.RunWithReusableSession<FakeSession, object>(ref current,
				_ => (null, false)), Is.Null);
			Assert.That(first.Disposed, Is.True);
			Assert.That(current, Is.Null);
		}

		[Test]
		public void WindowEventSourcePromotesOnOwnerChangeAndDemotesOnStreamFailure()
		{
			var available = false;
			var preferredAttempts = 0;
			var fallbackStarts = 0;
			Action availabilityChanged = null;
			Action<Exception> streamError = null;
			var fallbacks = new List<TrackingDisposable>();
			var preferred = new TrackingDisposable();

			using var source = new RecoveringSubscription(
				onError =>
				{
					preferredAttempts++;
					streamError = onError;
					return preferred;
				},
				() =>
				{
					fallbackStarts++;
					var fallback = new TrackingDisposable();
					fallbacks.Add(fallback);
					return fallback;
				},
				() => available,
				handler =>
				{
					availabilityChanged = handler;
					return new TrackingDisposable();
				},
				retryIntervalMs: 60_000);
			source.Start();

			Assert.That(source.IsPreferred, Is.False);
			Assert.That(preferredAttempts, Is.Zero, "known owner absence must not consume retry attempts");
			Assert.That(fallbackStarts, Is.EqualTo(1));

			available = true;
			availabilityChanged();
			Assert.That(source.IsPreferred, Is.True);
			Assert.That(preferredAttempts, Is.EqualTo(1));
			Assert.That(fallbacks[0].Disposed, Is.True);

			streamError(new IOException("signal stream failed"));
			Assert.That(source.IsPreferred, Is.False);
			Assert.That(preferred.Disposed, Is.True);
			Assert.That(fallbackStarts, Is.EqualTo(2));
		}

		private sealed class FakeSession : IDisposable
		{
			internal bool Disposed;
			public void Dispose() => Disposed = true;
		}

		private sealed class TrackingDisposable : IDisposable
		{
			internal bool Disposed { get; private set; }
			public void Dispose() => Disposed = true;
		}

		private sealed class TransientProbeOverlayBackend : IWaylandBackend
		{
			public string Name => "shell-test";
			public bool CanAttemptImageOverlay => true;

			public bool TryGetCursorPos(out int x, out int y)
			{
				x = y = 0;
				return false;
			}
		}
	}
}
#endif
