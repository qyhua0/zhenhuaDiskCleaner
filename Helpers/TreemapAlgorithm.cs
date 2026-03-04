using System.Windows;
using System.Collections.Generic;
using ZhenhuaDiskCleaner.Models;

namespace ZhenhuaDiskCleaner.Helpers
{
    public static class TreemapAlgorithm
    {
        public static List<TreemapNode> Build(FileNode root, Rect bounds, int maxDepth = 3)
        {
            var result = new List<TreemapNode>();
            if (root.Size == 0 || bounds.Width < 2 || bounds.Height < 2) return result;
            var children = new List<FileNode>(root.Children);
            children.Sort((a, b) => b.Size.CompareTo(a.Size));
            result.AddRange(Squarify(children, bounds, maxDepth, 0));
            return result;
        }

        private static List<TreemapNode> Squarify(List<FileNode> items, Rect bounds, int maxDepth, int depth)
        {
            var result = new List<TreemapNode>();
            if (items.Count == 0 || bounds.Width < 2 || bounds.Height < 2) return result;

            long total = 0; foreach (var i in items) total += i.Size;
            if (total == 0) return result;

            var remaining = new List<FileNode>(items);
            var cur = bounds;

            while (remaining.Count > 0 && cur.Width >= 2 && cur.Height >= 2)
            {
                bool horiz = cur.Width >= cur.Height;
                double shortSide = horiz ? cur.Height : cur.Width;
                double longSide  = horiz ? cur.Width  : cur.Height;
                double area = cur.Width * cur.Height;
                long remTotal = 0; foreach (var r in remaining) remTotal += r.Size;

                var row = new List<FileNode>();
                foreach (var item in remaining)
                {
                    var cand = new List<FileNode>(row) { item };
                    if (row.Count == 0 || Worst(row, shortSide, area, remTotal) >= Worst(cand, shortSide, area, remTotal))
                        row.Add(item);
                    else break;
                }

                long rowTotal = 0; foreach (var r in row) rowTotal += r.Size;
                double rowLen = (double)rowTotal / remTotal * longSide;

                double pos = 0;
                foreach (var item in row)
                {
                    double itemLen = rowTotal > 0 ? (double)item.Size / rowTotal * shortSide : 0;
                    Rect itemBounds = horiz
                        ? new Rect(cur.X + pos, cur.Y, itemLen, rowLen)
                        : new Rect(cur.X, cur.Y + pos, rowLen, itemLen);

                    var node = new TreemapNode { FileNode = item, Bounds = itemBounds, Level = depth };
                    result.Add(node);

                    if (depth < maxDepth - 1 && item.IsDirectory && item.Children.Count > 0
                        && itemBounds.Width > 20 && itemBounds.Height > 20)
                    {
                        var cb = new Rect(itemBounds.X + 1, itemBounds.Y + 14,
                            System.Math.Max(2, itemBounds.Width - 2),
                            System.Math.Max(2, itemBounds.Height - 15));
                        var sub = new List<FileNode>(item.Children);
                        sub.Sort((a, b) => b.Size.CompareTo(a.Size));
                        node.Children.AddRange(Squarify(sub, cb, maxDepth, depth + 1));
                    }
                    pos += itemLen;
                    remaining.Remove(item);
                }

                if (horiz)
                    cur = new Rect(cur.X, cur.Y + rowLen, cur.Width, System.Math.Max(0, cur.Height - rowLen));
                else
                    cur = new Rect(cur.X + rowLen, cur.Y, System.Math.Max(0, cur.Width - rowLen), cur.Height);
            }
            return result;
        }

        private static double Worst(List<FileNode> row, double s, double totalArea, long total)
        {
            if (row.Count == 0) return double.MaxValue;
            long rowSum = 0; long maxS = 0; long minS = long.MaxValue;
            foreach (var r in row) { rowSum += r.Size; if (r.Size > maxS) maxS = r.Size; if (r.Size < minS) minS = r.Size; }
            double rowArea = (double)rowSum / total * totalArea;
            double maxA = (double)maxS / total * totalArea;
            double minA = (double)minS / total * totalArea;
            double s2 = s * s, ra2 = rowArea * rowArea;
            if (ra2 == 0 || minA == 0) return double.MaxValue;
            return System.Math.Max(s2 * maxA / ra2, ra2 / (s2 * minA));
        }
    }
}
