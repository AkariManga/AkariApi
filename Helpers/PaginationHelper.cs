namespace AkariApi.Helpers
{
    public static class PaginationHelper
    {
        /// <summary>
        /// Clamps the page and pageSize to valid bounds.
        /// </summary>
        /// <param name="page">The page number.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <param name="maxPageSize">The maximum allowed page size (default 100).</param>
        /// <param name="defaultPageSize">The default page size if invalid (default 20).</param>
        /// <returns>A tuple with clamped page and pageSize.</returns>
        public static (int page, int pageSize) ClampPagination(int page, int pageSize, int maxPageSize = 100, int defaultPageSize = 20)
        {
            if (pageSize > maxPageSize)
                pageSize = maxPageSize;
            if (pageSize < 1)
                pageSize = defaultPageSize;
            if (page < 1)
                page = 1;
            return (page, pageSize);
        }
    }
}