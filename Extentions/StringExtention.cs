namespace Coflnet.Payments.Services
{
    public static class StringExtention
    {
        public static string Truncate(this string newSlug, int maxLenght)
        {
            return newSlug.Length > maxLenght ? newSlug.Substring(0, maxLenght) : newSlug;
        }
    }
}