using System.Collections.Generic;
using Coflnet.Payments.Models;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Payments.Services
{
    public class TokenService
    {
        private Dictionary<string, TokenSettings> Tokens;
        private Dictionary<ushort, TokenSettings> TokensViaId;

        public TokenService(IConfiguration config)
        {
            List<TokenSettings> Settings = new List<TokenSettings>();
            config.Bind("PAYMENT_TOKENS", Settings);
            ushort id = 0;
            foreach (var item in Settings)
            {
                id++;
                if (Tokens.ContainsKey(item.Token))
                    throw new System.Exception("PAYMENT_TOKENS contains two tokens that are the same");
                if (TokensViaId.ContainsKey(item.Id))
                    throw new System.Exception("PAYMENT_TOKENS contains two ids that are the same");
                if (TokensViaId.ContainsKey(id))
                    throw new System.Exception($"PAYMENT_TOKENS contains an id that is the same as a token with an autogenerated id via the index {id}");
                var tokenId = id;
                if (item.Id != 0)
                    tokenId = item.Id;
                TokensViaId[tokenId] = item;
                Tokens[item.Token] = item;
            }
        }

        /// <summary>
        /// Does the given token have read access
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        public bool CanTokenRead(string token)
        {
            return GetTokenOrDefault(token) != null;
        }

        /// <summary>
        /// Does the given token have write permissions
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        public bool CanTokenWrite(string token)
        {
            var val = GetTokenOrDefault(token);
            return val != null && !val.Readonly;
        }

        /// <summary>
        /// Gets all settings assocciated with a token
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        public TokenSettings GetTokenOrDefault(string token)
        {
            return Tokens.GetValueOrDefault(token);
        }
    }
}