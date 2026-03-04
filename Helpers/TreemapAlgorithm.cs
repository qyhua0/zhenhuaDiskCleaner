using System.Windows;
using ZhenhuaDiskCleaner.Models;

namespace ZhenhuaDiskCleaner.Helpers
{
    public static class TreemapAlgorithm
    {
        public static List<TreemapNode> Build(FileNode root, Rect bounds)
        {
            var result = new List<TreemapNode>();
            if (bounds.Width < 1 || bounds.Height < 1) return result;

            var leaves = new List<FileNode>();
            CollectLeaves(root, leaves);
            if (leaves.Count == 0) return result;

            // 大小为0的文件赋予最小值1，保证都能显示
            foreach (var f in leaves)
                if (f.Size <= 0) f.Size = 1;

            long total = leaves.Sum(f => f.Size);
            leaves.Sort((a, b) => b.Size.CompareTo(a.Size));

            Layout(leaves, 0, leaves.Count - 1, bounds, total, true, result);
            return result;
        }

        private static void CollectLeaves(FileNode node, List<FileNode> leaves)
        {
            if (!node.IsDirectory)
            { leaves.Add(node); return; }
            foreach (var c in node.Children)
                CollectLeaves(c, leaves);
        }

        /// <summary>
        /// 递归切片布局：
        /// 每次按面积比例将当前矩形切分为两半，
        /// 左/上半放前一组，右/下半放后一组，方向交替。
        /// </summary>
        private static void Layout(
            List<FileNode> items, int lo, int hi,
            Rect rect, long total,
            bool splitHoriz,
            List<TreemapNode> output)
        {
            if (lo > hi) return;
            if (rect.Width < 0.5 || rect.Height < 0.5) return;

            // 只剩一个节点，直接占满当前矩形
            if (lo == hi)
            {
                output.Add(new TreemapNode
                {
                    FileNode = items[lo],
                    Bounds = new Rect(
                        Math.Round(rect.X, 1), Math.Round(rect.Y, 1),
                        Math.Max(1, Math.Round(rect.Width, 1)),
                        Math.Max(1, Math.Round(rect.Height, 1))),
                    Level = 0
                });
                return;
            }

            // 找中间分割点：使左右两组面积之和尽量接近各占一半
            int mid = FindMid(items, lo, hi, total);

            long leftSum = Sum(items, lo, mid);
            long rightSum = Sum(items, mid + 1, hi);
            long bothSum = leftSum + rightSum;
            if (bothSum == 0) return;

            double ratio = (double)leftSum / bothSum;

            Rect leftRect, rightRect;
            if (splitHoriz)
            {
                double splitX = rect.X + rect.Width * ratio;
                leftRect = new Rect(rect.X, rect.Y, rect.Width * ratio, rect.Height);
                rightRect = new Rect(splitX, rect.Y, rect.Width * (1 - ratio), rect.Height);
            }
            else
            {
                double splitY = rect.Y + rect.Height * ratio;
                leftRect = new Rect(rect.X, rect.Y, rect.Width, rect.Height * ratio);
                rightRect = new Rect(rect.X, splitY, rect.Width, rect.Height * (1 - ratio));
            }

            // 交替方向递归
            Layout(items, lo, mid, leftRect, leftSum, !splitHoriz, output);
            Layout(items, mid + 1, hi, rightRect, rightSum, !splitHoriz, output);
        }

        // 找最优分割点，使两组面积尽量均衡
        private static int FindMid(List<FileNode> items, int lo, int hi, long total)
        {
            long half = total / 2;
            long acc = 0;
            for (int i = lo; i < hi; i++)
            {
                acc += items[i].Size;
                if (acc >= half) return i;
            }
            return hi - 1;
        }

        private static long Sum(List<FileNode> items, int lo, int hi)
        {
            long s = 0;
            for (int i = lo; i <= hi; i++) s += items[i].Size;
            return s;
        }
    }
}