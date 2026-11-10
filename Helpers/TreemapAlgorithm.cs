using System.Windows;
using ZhenhuaDiskCleaner.Models;

namespace ZhenhuaDiskCleaner.Helpers
{
    /// <summary>
    /// 矩形树图布局（Squarified 的简化版：按面积中点二分）。
    /// 全部遍历都用显式栈，不用递归——整盘扫描后叶子节点是几十万级，
    /// 极端偏斜的大小分布会让递归深度达到叶子数量级，直接爆栈。
    /// </summary>
    public static class TreemapAlgorithm
    {
        /// <summary>
        /// 二分深度上限。超过说明面积分布极度偏斜（一个巨型文件 + 一堆碎文件），
        /// 再切下去每块都只剩一两个文件、矩形也几乎没有区别，改用条状兜底平铺。
        /// </summary>
        private const int MaxDepth = 200;

        public static List<TreemapNode> Build(FileNode root, Rect bounds)
        {
            var result = new List<TreemapNode>();
            if (bounds.Width < 1 || bounds.Height < 1) return result;

            // 按目录分组收集叶子节点，同目录在一起
            var leaves = new List<FileNode>();
            CollectByDirectory(root, leaves);
            if (leaves.Count == 0) return result;

            // 0 字节文件在矩形树图里会算出 0 面积从而消失；这里用一个"布局用尺寸"
            // 数组给它一个最小权重。以前是直接 f.Size = 1 改写模型，
            // 结果右键属性、文件列表里显示的大小被布局算法污染了。
            var sz = new long[leaves.Count];
            long total = 0;
            for (int i = 0; i < leaves.Count; i++)
            {
                long s = leaves[i].Size;
                if (s <= 0) s = 1;
                sz[i] = s;
                total += s;
            }
            if (total <= 0) return result;

            Layout(leaves, sz, 0, leaves.Count - 1, bounds, total, true, result);
            return result;
        }

        /// <summary>
        /// 深度优先：先输出当前目录的直属文件，再按大小降序进入子目录，
        /// 保证同目录文件在叶子序列里相邻（视觉上成块）。显式栈实现。
        /// </summary>
        private static void CollectByDirectory(FileNode root, List<FileNode> leaves)
        {
            var stack = new Stack<FileNode>();
            if (root.IsDirectory) stack.Push(root);
            else { leaves.Add(root); return; }

            while (stack.Count > 0)
            {
                var dir = stack.Pop();

                foreach (var c in dir.Children)
                    if (!c.IsDirectory) leaves.Add(c);

                // 栈是 LIFO，所以这里要"升序压栈"，弹出来才是降序
                var dirs = new List<FileNode>();
                foreach (var c in dir.Children)
                    if (c.IsDirectory) dirs.Add(c);
                dirs.Sort((a, b) => a.Size.CompareTo(b.Size));

                foreach (var d in dirs) stack.Push(d);
            }
        }

        /// <summary>栈上的一个待切分区域（原来的递归帧）。</summary>
        private readonly struct Slice
        {
            public readonly int Lo, Hi;
            public readonly Rect Rect;
            public readonly long Total;
            public readonly bool SplitHoriz;
            public readonly int Depth;

            public Slice(int lo, int hi, Rect rect, long total, bool splitHoriz, int depth)
            {
                Lo = lo; Hi = hi; Rect = rect; Total = total;
                SplitHoriz = splitHoriz; Depth = depth;
            }
        }

        private static void Layout(
            List<FileNode> items, long[] sz, int lo, int hi,
            Rect rect, long total, bool splitHoriz,
            List<TreemapNode> output)
        {
            var stack = new Stack<Slice>();
            stack.Push(new Slice(lo, hi, rect, total, splitHoriz, 0));

            while (stack.Count > 0)
            {
                var s = stack.Pop();
                if (s.Lo > s.Hi) continue;
                if (s.Rect.Width < 0.5 || s.Rect.Height < 0.5) continue;

                if (s.Lo == s.Hi)
                {
                    Add(items[s.Lo], s.Rect, output);
                    continue;
                }

                if (s.Depth >= MaxDepth)
                {
                    EmitStrip(items, sz, s, output);
                    continue;
                }

                int mid = FindMid(sz, s.Lo, s.Hi, out long leftSum);
                long rightSum = s.Total - leftSum;
                long bothSum = leftSum + rightSum;
                if (bothSum <= 0) continue;

                double ratio = (double)leftSum / bothSum;

                Rect leftRect, rightRect;
                if (s.SplitHoriz)
                {
                    double splitX = s.Rect.X + s.Rect.Width * ratio;
                    leftRect  = new Rect(s.Rect.X, s.Rect.Y, s.Rect.Width * ratio, s.Rect.Height);
                    rightRect = new Rect(splitX, s.Rect.Y, s.Rect.Width * (1 - ratio), s.Rect.Height);
                }
                else
                {
                    double splitY = s.Rect.Y + s.Rect.Height * ratio;
                    leftRect  = new Rect(s.Rect.X, s.Rect.Y, s.Rect.Width, s.Rect.Height * ratio);
                    rightRect = new Rect(s.Rect.X, splitY, s.Rect.Width, s.Rect.Height * (1 - ratio));
                }

                stack.Push(new Slice(mid + 1, s.Hi, rightRect, rightSum, !s.SplitHoriz, s.Depth + 1));
                stack.Push(new Slice(s.Lo, mid, leftRect, leftSum, !s.SplitHoriz, s.Depth + 1));
            }
        }

        /// <summary>深度超限的兜底：按面积比例沿短边切成等宽条，一次铺完，不再递归。</summary>
        private static void EmitStrip(List<FileNode> items, long[] sz, Slice s, List<TreemapNode> output)
        {
            int count = s.Hi - s.Lo + 1;
            if (count <= 0 || s.Total <= 0) return;

            bool horiz = s.Rect.Width >= s.Rect.Height;
            double origin = horiz ? s.Rect.X : s.Rect.Y;
            double span = horiz ? s.Rect.Width : s.Rect.Height;

            for (int i = s.Lo; i <= s.Hi; i++)
            {
                double share = span * ((double)sz[i] / s.Total);
                if (horiz)
                    Add(items[i], new Rect(origin, s.Rect.Y, share, s.Rect.Height), output);
                else
                    Add(items[i], new Rect(s.Rect.X, origin, s.Rect.Width, share), output);
                origin += share;
            }
        }

        private static void Add(FileNode node, Rect rect, List<TreemapNode> output)
        {
            output.Add(new TreemapNode
            {
                FileNode = node,
                Bounds = new Rect(
                    Math.Round(rect.X, 1), Math.Round(rect.Y, 1),
                    Math.Max(1, Math.Round(rect.Width, 1)),
                    Math.Max(1, Math.Round(rect.Height, 1))),
                Level = 0
            });
        }

        /// <summary>
        /// 找切分点：累加到总面积一半的那个下标（保证 lo..mid 非空、mid+1..hi 非空）。
        /// 顺带把 leftSum 带出去，省掉调用方两次 O(n) 的重复求和。
        /// </summary>
        private static int FindMid(long[] sz, int lo, int hi, out long leftSum)
        {
            long half = 0;
            for (int i = lo; i <= hi; i++) half += sz[i];
            half /= 2;

            long acc = 0;
            for (int i = lo; i < hi; i++)
            {
                acc += sz[i];
                if (acc >= half)
                {
                    leftSum = acc;
                    return i;
                }
            }
            leftSum = acc;
            return hi - 1;
        }
    }
}
