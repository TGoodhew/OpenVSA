using System;
using System.Diagnostics;
using System.Threading;

namespace OpenVSA.Core.Threading
{
    /// <summary>
    /// Thread-affinity assertions for the layer boundaries of <c>REQ-NFR-010</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The thread topology — UI thread never performs I/O or DSP, the acquisition pump never
    /// touches WPF — is the kind of rule that holds until one convenient call, and then fails as an
    /// intermittent hang or a torn frame a long way from the cause. The requirement's acceptance
    /// criterion is therefore an assertion helper present at the boundaries, not a paragraph in a
    /// design document.
    /// </para>
    /// <para>
    /// <strong>Debug builds only.</strong> Every method is
    /// <see cref="ConditionalAttribute">[Conditional]</see> on <c>OPENVSA_THREAD_ASSERTS</c>, which
    /// <c>Directory.Build.props</c> defines for Debug alone, so the calls are erased from a Release
    /// build by the compiler rather than tested for at run time. That includes
    /// <see cref="MarkUiThread"/>: with the assertions gone there is nothing to mark.
    /// </para>
    /// <para>
    /// <strong>No WPF dependency.</strong> This lives in L1 and is used from the DSP layer, which
    /// must not know that a <c>Dispatcher</c> exists. The UI thread identifies itself once at
    /// start-up instead.
    /// </para>
    /// </remarks>
    public static class ThreadAffinity
    {
        /// <summary>Managed id of the UI thread, or 0 before one has been marked.</summary>
        private static int _uiThreadId;

        /// <summary>
        /// Records the calling thread as the UI thread. Called once, from application start-up.
        /// </summary>
        /// <remarks>
        /// Idempotent for the same thread. A second call from a <em>different</em> thread is itself
        /// a topology error: WPF applications have one dispatcher thread, and a test harness that
        /// marked two would silently disable half the assertions.
        /// </remarks>
        /// <exception cref="InvalidOperationException">A different thread has already been marked.</exception>
        [Conditional("OPENVSA_THREAD_ASSERTS")]
        public static void MarkUiThread()
        {
            int id = Thread.CurrentThread.ManagedThreadId;
            int existing = Interlocked.CompareExchange(ref _uiThreadId, id, 0);

            if (existing != 0 && existing != id)
            {
                throw new InvalidOperationException(
                    "Thread " + existing + " is already the UI thread; thread " + id +
                    " cannot also be (REQ-NFR-010).");
            }
        }

        /// <summary>Forgets the marked UI thread. For tests that need to re-mark.</summary>
        [Conditional("OPENVSA_THREAD_ASSERTS")]
        public static void ClearUiThread()
        {
            Interlocked.Exchange(ref _uiThreadId, 0);
        }

        /// <summary>
        /// Asserts that the caller is on the UI thread.
        /// </summary>
        /// <param name="what">What is being done, for the message.</param>
        /// <exception cref="InvalidOperationException">The caller is not on the marked UI thread.</exception>
        [Conditional("OPENVSA_THREAD_ASSERTS")]
        public static void AssertOnUiThread(string what)
        {
            int ui = Volatile.Read(ref _uiThreadId);

            if (ui == 0 || ui == Thread.CurrentThread.ManagedThreadId)
            {
                return;
            }

            throw new InvalidOperationException(
                (what ?? "This") + " must run on the UI thread, but is running on thread " +
                Thread.CurrentThread.ManagedThreadId + " (REQ-NFR-010).");
        }

        /// <summary>
        /// Asserts that the caller is not on the UI thread.
        /// </summary>
        /// <param name="what">What is being done, for the message.</param>
        /// <exception cref="InvalidOperationException">The caller is on the marked UI thread.</exception>
        /// <remarks>
        /// Silent when no UI thread has been marked, which is the case in the headless test run and
        /// in the automation surface of <c>REQ-API-002</c>. An assertion that fired when there was
        /// no UI at all would be asserting something the requirement does not say.
        /// </remarks>
        [Conditional("OPENVSA_THREAD_ASSERTS")]
        public static void AssertNotOnUiThread(string what)
        {
            int ui = Volatile.Read(ref _uiThreadId);

            if (ui == 0 || ui != Thread.CurrentThread.ManagedThreadId)
            {
                return;
            }

            throw new InvalidOperationException(
                (what ?? "This") + " must not run on the UI thread (REQ-NFR-010): the dispatcher " +
                "may not perform I/O or DSP.");
        }
    }
}
