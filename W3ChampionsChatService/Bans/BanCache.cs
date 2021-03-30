﻿using System;

namespace W3ChampionsChatService.Bans
{
    public class BanCache
    {
        private DateTimeOffset _wasUpdated = DateTimeOffset.MinValue;
        public bool HasValue => DateTimeOffset.UtcNow - _wasUpdated < TimeSpan.FromMinutes(10);
        public BannedPlayerResponse Cache { get; private set; }

        public void SetCache(BannedPlayerResponse cache)
        {
            Cache = cache;
            _wasUpdated = DateTimeOffset.UtcNow;
        }
    }
}