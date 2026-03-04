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

            // 按目录分组收集叶子节点，同目录在一起
            var leaves = new List<FileNode>();
            CollectByDirectory(root, leaves);
            if (leaves.Count == 0) return result;

            foreach (var f in leaves)
                if (f.Size <= 0) f.Size = 1;

            long total = leaves.Sum(f => f.Size);
            Layout(leaves, 0, leaves.Count - 1, bounds, total, true, result);
            return result;
        }

        // 深度优先：先输出当前目录的文件，再递归子目录
        // 保证同目录文件相邻
        private static void CollectByDirectory(FileNode node, List<FileNode> leaves)
        {
            if (!node.IsDirectory)
            { leaves.Add(node); return; }

            // 先加本目录下的直属文件
            foreach (var c in node.Children.Where(c => !c.IsDirectory))
                leaves.Add(c);

            // 再递归子目录（按子目录大小降序，大目录优先，视觉更整齐）
            foreach (var c in node.Children
                         .Where(c => c.IsDirectory)
                         .OrderByDescending(c => c.Size))
                CollectByDirectory(c, leaves);
        }

        private static void Layout(
            List<FileNode> items, int lo, int hi,
            Rect rect, long total,
            bool splitHoriz,
            List<TreemapNode> output)
        {
            if (lo > hi) return;
            if (rect.Width < 0.5 || rect.Height < 0.5) return;

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

            int mid = FindMid(items, lo, hi);
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

            Layout(items, lo, mid, leftRect, leftSum, !splitHoriz, output);
            Layout(items, mid + 1, hi, rightRect, rightSum, !splitHoriz, output);
        }

        private static int FindMid(List<FileNode> items, int lo, int hi)
        {
            long total = Sum(items, lo, hi);
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