using System;

namespace Coflnet.Payments.Services
{
    /// <summary>
    /// Custom api exceptions that should be displayed to the user
    /// </summary>
    public class ApiException : Exception
    {
        public string Slug { get; }

        public ApiException(string message) : base(message)
        {
        }

        public ApiException(string message, string slug) : this(message)
        {
            Slug = slug;
        }
    }
}