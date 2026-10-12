using System;
using System.Threading;

namespace HVX.Controls
{
    // Exact order statistic for the display baseline. It never modifies the
    // caller's samples and is not used to decide acquisition/range quality.
    internal static class PressureDisplayMedian
    {
        internal static double Calculate(double[] values, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            if (values == null || values.Length == 0) return 0;
            double[] copy = (double[])values.Clone();
            int middle = copy.Length / 2;
            double upper = Select(copy, middle, cancel);
            if ((copy.Length & 1) != 0) return upper;
            double lower = copy[0];
            for (int i = 1; i < middle; i++)
            {
                if ((i & 16383) == 0) cancel.ThrowIfCancellationRequested();
                if (copy[i].CompareTo(lower) > 0) lower = copy[i];
            }
            cancel.ThrowIfCancellationRequested();
            // Keep the previous sorted median's arithmetic, including overflow.
            return (lower + upper) * .5;
        }

        static double Select(double[] values, int order, CancellationToken cancel)
        {
            int left = 0, right = values.Length - 1, depth = 0;
            for (int n = values.Length; n > 1; n >>= 1) depth += 2;
            while (left < right)
            {
                cancel.ThrowIfCancellationRequested();
                if (depth-- <= 0)
                {
                    // Deterministic bound for adversarial pivot sequences. The
                    // bounded heapsort remains cancellable, unlike Array.Sort.
                    HeapSort(values, left, right, cancel);
                    return values[order];
                }
                double a = values[left], b = values[left + (right - left) / 2], c = values[right];
                double pivot = a.CompareTo(b) < 0 ? (b.CompareTo(c) < 0 ? b : a.CompareTo(c) < 0 ? c : a)
                    : (a.CompareTo(c) < 0 ? a : b.CompareTo(c) < 0 ? c : b);
                int less = left, current = left, greater = right, scanned = 0;
                while (current <= greater)
                {
                    if ((scanned++ & 16383) == 0) cancel.ThrowIfCancellationRequested();
                    int comparison = values[current].CompareTo(pivot);
                    if (comparison < 0) Swap(values, less++, current++);
                    else if (comparison > 0) Swap(values, current, greater--);
                    else current++;
                }
                if (order < less) right = less - 1;
                else if (order > greater) left = greater + 1;
                else return values[order];
            }
            cancel.ThrowIfCancellationRequested();
            return values[order];
        }

        static void HeapSort(double[] values, int left, int right, CancellationToken cancel)
        {
            int length = right - left + 1;
            for (int i = length / 2 - 1; i >= 0; i--)
            {
                if ((i & 16383) == 0) cancel.ThrowIfCancellationRequested();
                Sift(values, left, i, length);
            }
            for (int end = length - 1; end > 0; end--)
            {
                if ((end & 16383) == 0) cancel.ThrowIfCancellationRequested();
                Swap(values, left, left + end);
                Sift(values, left, 0, end);
            }
        }
        static void Sift(double[] values, int offset, int root, int length)
        {
            while (root < length / 2)
            {
                int child = root * 2 + 1;
                if (child + 1 < length && values[offset + child].CompareTo(values[offset + child + 1]) < 0) child++;
                if (values[offset + root].CompareTo(values[offset + child]) >= 0) break;
                Swap(values, offset + root, offset + child);
                root = child;
            }
        }
        static void Swap(double[] values, int a, int b)
        { double temporary = values[a]; values[a] = values[b]; values[b] = temporary; }
    }
}
