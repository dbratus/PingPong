using System;

namespace PingPong.Engine
{
    static class WeightedRandom
    {
        public static int GetIndex(Random rng, Span<double> weights)
        {
            if (weights.Length == 1)
                return 0;

            Span<double> brackets = stackalloc double[weights.Length];

            brackets[0] = weights[0];

            for (int i = 1; i < weights.Length; ++i)
                brackets[i] = brackets[i-1] + weights[i];

            int idx = brackets.BinarySearch(rng.NextDouble() * brackets[brackets.Length - 1]);
            if (idx > 0)
                return idx;

            return ~idx;
        }
    }
}