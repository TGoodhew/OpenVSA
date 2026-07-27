using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// One single-threaded-apartment thread, with a dispatcher, shared by every test that builds a
    /// whole <see cref="ShellWindow"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One thread, not one per test.</strong> Syncfusion's <c>DockingManager</c> keeps
    /// thread-affine state from the first one constructed, so a second shell built on a
    /// <em>different</em> STA thread fails with "the calling thread cannot access this object
    /// because a different thread owns it" — even when the two tests run in sequence. Serialising
    /// the tests is not enough; they have to share the thread.
    /// </para>
    /// <para>
    /// <strong>This only fails in a full run.</strong> Each class passes on its own, because on its
    /// own it is the first to build a shell. That is worth knowing before spending an afternoon on
    /// a test that is green in isolation.
    /// </para>
    /// <para>
    /// The dispatcher is real and running, so a window shown here pumps messages as it would in the
    /// application — which is what lets the keyboard tests raise routed input at all.
    /// </para>
    /// </remarks>
    public sealed class ShellHost : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);

        private Dispatcher _dispatcher;

        /// <summary>Starts the thread and waits for its dispatcher.</summary>
        public ShellHost()
        {
            _thread = new Thread(() =>
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                _ready.Set();

                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "OpenVSA shell tests",
            };

            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            _ready.Wait(TimeSpan.FromSeconds(10.0));

            Assert.NotNull(_dispatcher);
        }

        /// <summary>
        /// Runs a test body on the shared thread.
        /// </summary>
        /// <param name="body">The body.</param>
        /// <remarks>
        /// The exception is captured and rethrown on the calling thread, so an assertion failure is
        /// reported as itself rather than as a thread that died.
        /// </remarks>
        public void Run(Action body)
        {
            ExceptionDispatchInfo failure = null;

            _dispatcher.Invoke(() =>
            {
                try
                {
                    body();
                }
                catch (Exception caught)
                {
                    failure = ExceptionDispatchInfo.Capture(caught);
                }
            });

            if (failure != null)
            {
                failure.Throw();
            }
        }

        /// <summary>Shuts the thread down.</summary>
        public void Dispose()
        {
            if (_dispatcher != null)
            {
                _dispatcher.InvokeShutdown();
            }

            _thread.Join(TimeSpan.FromSeconds(5.0));
            _ready.Dispose();
        }
    }

    /// <summary>The tests that build a whole shell, sharing one thread.</summary>
    [CollectionDefinition("Shell")]
    public sealed class ShellCollection : ICollectionFixture<ShellHost>
    {
    }
}
