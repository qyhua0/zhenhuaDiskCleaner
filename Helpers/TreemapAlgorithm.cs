using System.Windows;
using ZhenhuaDiskCleaner.Models;

namespace ZhenhuaDiskCleaner.Helpers
{
    public static class TreemapAlgorithm
    {
        public static List<TreemapNode> Build(FileNode root, Rect bounds, int maxDepth = 4)
        {
            var result = new List<TreemapNode>();
            if (root.Size == 0 || bounds.Width < 2 || bounds.Height < 2) return result;

            // 直接对 root 的子节点做布局
            var children = root.Children
                .Where(c => c.Size > 0)
                .OrderByDescending(c => c.Size)
                .ToList();

            Squarify(children, bounds, maxDepth, 0, result);
            return result;
        }

        private static void Squarify(List<FileNode> items, Rect bounds,
            int maxDepth, int depth, List<TreemapNode> output)
        {
            if (items.Count == 0 || bounds.Width < 2 || bounds.Height < 2) return;

            long total = items.Sum(i => i.Size);
            if (total == 0) return;

            var remaining = new List<FileNode>(items);
            var cur = bounds;

            while (remaining.Count > 0 && cur.Width >= 2 && cur.Height >= 2)
            {
                bool horiz = cur.Width >= cur.Height;
                double shortSide = horiz ? cur.Height : cur.Width;
                double longSide = horiz ? cur.Width : cur.Height;
                double area = cur.Width * cur.Height;
                long remTotal = remaining.Sum(r => r.Size);

                // 找最优行
                var row = new List<FileNode>();
                foreach (var item in remaining)
                {
                    var candidate = new List<FileNode>(row) { item };
                    if (row.Count == 0 || Worst(row, shortSide, area, remTotal) >= Worst(candidate, shortSide, area, remTotal))
                        row.Add(item);
                    else
                        break;
                }

                long rowTotal = row.Sum(r => r.Size);
                double rowThick = remTotal > 0 ? (double)rowTotal / remTotal * longSide : 0;

                double pos = 0;
                foreach (var item in row)
                {
                    double itemLen = rowTotal > 0 ? (double)item.Size / rowTotal * shortSide : 0;
                    var itemBounds = horiz
                        ? new Rect(cur.X + pos, cur.Y, itemLen, rowThick)
                        : new Rect(cur.X, cur.Y + pos, rowThick, itemLen);

                    // 最小可见尺寸过滤
                    if (itemBounds.Width < 1 || itemBounds.Height < 1) { pos += itemLen; continue; }

                    var node = new TreemapNode { FileNode = item, Bounds = itemBounds, Level = depth };
                    output.Add(node);

                    // 递归：目录继续细分
                    if (depth < maxDepth - 1 && item.IsDirectory && item.Children.Count > 0
                        && itemBounds.Width > 16 && itemBounds.Height > 16)
                    {
                        var inner = new Rect(
                            itemBounds.X + 1, itemBounds.Y + 14,
                            Math.Max(2, itemBounds.Width - 2),
                            Math.Max(2, itemBounds.Height - 15));

                        var subItems = item.Children
                            .Where(c => c.Size > 0)
                            .OrderByDescending(c => c.Size)
                            .ToList();

                        Squarify(subItems, inner, maxDepth, depth + 1, node.Children);
                    }

                    pos += itemLen;
                    remaining.Remove(item);
                }

                // 收缩剩余空间
                if (horiz)
                    cur = new Rect(cur.X, cur.Y + rowThick, cur.Width, Math.Max(0, cur.Height - rowThick));
                else
                    cur = new Rect(cur.X + rowThick, cur.Y, Math.Max(0, cur.Width - rowThick), cur.Height);
            }
        }

        private static double Worst(List<FileNode> row, double s, double totalArea, long total)
        {
            if (row.Count == 0) return double.MaxValue;
            long rowSum = row.Sum(r => r.Size);
            long maxS = row.Max(r => r.Size);
            long minS = row.Min(r => r.Size);
            double rowArea = (double)rowSum / total * totalArea;
            double maxA = (double)maxS / total * totalArea;
            double minA = (double)minS / total * totalArea;
            if (rowArea == 0 || minA == 0) return double.MaxValue;
            return Math.Max(s * s * maxA / (rowArea * rowArea), rowArea * rowArea / (s * s * minA));
        }
    }
}