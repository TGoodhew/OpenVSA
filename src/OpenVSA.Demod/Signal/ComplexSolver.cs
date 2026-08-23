using System;

namespace OpenVSA.Demod.Signal
{
    /// <summary>
    /// Solves a small complex linear system, for the equaliser's normal equations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gaussian elimination with partial pivoting, on a matrix of a few tens of rows. The
    /// equaliser of <c>REQ-DEM-052</c> is specified as least-squares with LMS for parity, and a
    /// least-squares equaliser of <em>L</em> taps is an <em>L</em>×<em>L</em> solve; at the tap
    /// counts the requirement talks about, the cube of the tap count is a few thousand operations
    /// and the choice of algorithm is not where the time goes.
    /// </para>
    /// <para>
    /// <strong>Pivoting is not optional here.</strong> The autocorrelation matrix of a signal with
    /// a strong linear distortion is poorly conditioned in exactly the case the equaliser exists
    /// for, and elimination without pivoting on such a matrix produces coefficients that look
    /// plausible and make EVM worse.
    /// </para>
    /// </remarks>
    internal static class ComplexSolver
    {
        /// <summary>
        /// Solves <c>A x = b</c>.
        /// </summary>
        /// <param name="matrix">The matrix, row-major, <paramref name="order"/> squared entries.</param>
        /// <param name="right">The right-hand side, <paramref name="order"/> entries.</param>
        /// <param name="order">The system's order.</param>
        /// <returns>The solution, or <c>null</c> when the matrix is singular to working precision.</returns>
        internal static Iq[] Solve(Iq[] matrix, Iq[] right, int order)
        {
            var a = (Iq[])matrix.Clone();
            var b = (Iq[])right.Clone();

            for (int column = 0; column < order; column++)
            {
                int pivot = column;
                double best = a[(column * order) + column].MagnitudeSquared;

                for (int row = column + 1; row < order; row++)
                {
                    double candidate = a[(row * order) + column].MagnitudeSquared;

                    if (candidate > best)
                    {
                        best = candidate;
                        pivot = row;
                    }
                }

                if (best < 1e-24)
                {
                    return null;
                }

                if (pivot != column)
                {
                    SwapRows(a, b, column, pivot, order);
                }

                Iq diagonal = a[(column * order) + column];

                for (int row = column + 1; row < order; row++)
                {
                    Iq factor = Divide(a[(row * order) + column], diagonal);

                    if (factor.MagnitudeSquared < 1e-30)
                    {
                        continue;
                    }

                    for (int inner = column; inner < order; inner++)
                    {
                        a[(row * order) + inner] =
                            a[(row * order) + inner] - (factor * a[(column * order) + inner]);
                    }

                    b[row] = b[row] - (factor * b[column]);
                }
            }

            var solution = new Iq[order];

            for (int row = order - 1; row >= 0; row--)
            {
                Iq sum = b[row];

                for (int column = row + 1; column < order; column++)
                {
                    sum = sum - (a[(row * order) + column] * solution[column]);
                }

                solution[row] = Divide(sum, a[(row * order) + row]);
            }

            return solution;
        }

        private static void SwapRows(Iq[] a, Iq[] b, int first, int second, int order)
        {
            for (int column = 0; column < order; column++)
            {
                Iq held = a[(first * order) + column];

                a[(first * order) + column] = a[(second * order) + column];
                a[(second * order) + column] = held;
            }

            Iq heldRight = b[first];

            b[first] = b[second];
            b[second] = heldRight;
        }

        private static Iq Divide(Iq numerator, Iq denominator)
        {
            double magnitude = denominator.MagnitudeSquared;

            if (magnitude < 1e-300)
            {
                return Iq.Zero;
            }

            return (numerator * denominator.Conjugate()) / magnitude;
        }
    }
}
