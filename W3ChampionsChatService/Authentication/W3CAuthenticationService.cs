using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace W3ChampionsChatService.Authentication
{
    public class W3CAuthenticationService : IW3CAuthenticationService
    {
        private readonly TokenCache _tokenCache;
        private static readonly string IdentificationApiUrl = Environment.GetEnvironmentVariable("IDENTIFICATION_SERVICE_URI") ?? "https://identification-service.test.w3champions.com";

        public W3CAuthenticationService(TokenCache tokenCache)
        {
            _tokenCache = tokenCache;
        }

        public async Task<W3CUserAuthenticationDto> GetUserByToken(string bearer)
        {
            try
            {

                if (_tokenCache.TryGetValue(bearer, out var user))
                {
                    return user;
                }

                var httpClient = new HttpClient();
                httpClient.BaseAddress = new Uri(IdentificationApiUrl);
                var result = await httpClient.GetAsync($"/api/oauth/battleTag?bearer={bearer}");
                if (result.IsSuccessStatusCode)
                {
                    var content = await result.Content.ReadAsStringAsync();
                    var deserializeObject = JsonConvert.DeserializeObject<W3CUserAuthenticationDto>(content);
                    _tokenCache.Add(bearer, deserializeObject);
                    return deserializeObject;
                }

                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public interface IW3CAuthenticationService
    {
        Task<W3CUserAuthenticationDto> GetUserByToken(string bearer);
    }

    public class W3CUserAuthenticationDto
    {
        public string BattleTag { get; set; }
        public string Name { get; set; }
        public bool isAdmin { get; set; }
    }


    public class TokenCache : Dictionary<string, W3CUserAuthenticationDto>
    {
    }
}