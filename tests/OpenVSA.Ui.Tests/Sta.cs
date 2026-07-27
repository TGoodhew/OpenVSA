using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// Runs a test body on a single-threaded-apartment thread.
    /// </summary>
    /// <remarks>
    /// WPF elements can only be created on an STA thread, and the test runner's threads are not.
    /// The exception is captured and rethrown on the calling thread so an assertion failure is
    /// reported as itself rather than as a thread that died.
    /// </remarks>
    internal static class Sta
    {
        internal static void Run(Action body)
        {
            ExceptionDispatchInfo failure = null;

            var thread = new Thread(() =>
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

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null)
            {
                failure.Throw();
            }
        }
    }
}
