namespace DisplayServer.Compositor
{
    public static class DirtyRegionTracker
    {
        public static bool Clip(ref uint x, ref uint y, ref uint w, ref uint h, uint maxWidth, uint maxHeight)
        {
            if (maxWidth == 0 || maxHeight == 0) return false;
            if (x >= maxWidth || y >= maxHeight) return false;

            if (w > maxWidth - x)
            {
                w = maxWidth - x;
            }
            if (h > maxHeight - y)
            {
                h = maxHeight - y;
            }

            return w > 0 && h > 0;
        }
    }
}
