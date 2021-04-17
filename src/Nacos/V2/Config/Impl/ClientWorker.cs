﻿namespace Nacos.V2.Config.Impl
{
    using Microsoft.Extensions.Logging;
    using Nacos.V2.Common;
    using Nacos.V2.Config.Abst;
    using Nacos.V2.Config.FilterImpl;
    using Nacos.V2.Config.Utils;
    using Nacos.V2.Utils;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Threading.Tasks;

    public class ClientWorker : IDisposable
    {
        private readonly ILogger _logger;

        private ConfigFilterChainManager _configFilterChainManager;

        private ConcurrentDictionary<string, CacheData> _cacheMap = new ConcurrentDictionary<string, CacheData>();

        private IConfigTransportClient _agent;

        public ClientWorker(ILogger logger, ConfigFilterChainManager configFilterChainManager, NacosSdkOptions options)
        {
            _logger = logger;
            _configFilterChainManager = configFilterChainManager;

            ServerListManager serverListManager = new ServerListManager(logger, options);

            _agent = options.ConfigUseRpc
                ? new ConfigRpcTransportClient(logger, options, serverListManager, _cacheMap)
                : new ConfigHttpTransportClient(logger, options, serverListManager, _cacheMap);
        }

        public async Task AddTenantListeners(string dataId, string group, List<IListener> listeners)
        {
            group = ParamUtils.Null2DefaultGroup(group);
            string tenant = _agent.GetTenant();

            CacheData cache = AddCacheDataIfAbsent(dataId, group, tenant);
            foreach (var listener in listeners)
            {
                cache.AddListener(listener);
            }

            if (!cache.IsListenSuccess)
            {
                await _agent.NotifyListenConfigAsync();
            }
        }

        internal async Task AddTenantListenersWithContent(string dataId, string group, string content, List<IListener> listeners)
        {
            group = ParamUtils.Null2DefaultGroup(group);
            string tenant = _agent.GetTenant();
            CacheData cache = AddCacheDataIfAbsent(dataId, group, tenant);
            cache.SetContent(content);
            foreach (var listener in listeners)
            {
                cache.AddListener(listener);
            }

            // if current cache is already at listening status,do not notify.
            if (!cache.IsListenSuccess)
            {
                await _agent.NotifyListenConfigAsync();
            }
        }

        public async Task RemoveTenantListener(string dataId, string group, IListener listener)
        {
            group = ParamUtils.Null2DefaultGroup(group);
            string tenant = _agent.GetTenant();

            CacheData cache = GetCache(dataId, group, tenant);
            if (cache != null)
            {
                cache.RemoveListener(listener);
                if ((cache.GetListeners()?.Count ?? 0) > 0)
                {
                    await _agent.RemoveCacheAsync(dataId, group);
                }
            }
        }

        public CacheData AddCacheDataIfAbsent(string dataId, string group, string tenant)
        {
            CacheData cache = GetCache(dataId, group, tenant);

            if (cache != null) return cache;

            string key = GroupKey.GetKey(dataId, group, tenant);
            CacheData cacheFromMap = GetCache(dataId, group, tenant);

            // multiple listeners on the same dataid+group and race condition,so double check again
            // other listener thread beat me to set to cacheMap
            if (cacheFromMap != null)
            {
                cache = cacheFromMap;

                // reset so that server not hang this check
                cache.IsInitializing = true;
            }
            else
            {
                cache = new CacheData(_configFilterChainManager, _agent.GetName(), dataId, group, tenant);

                int taskId = _cacheMap.Count / CacheData.PerTaskConfigSize;
                cache.TaskId = taskId;
            }

            _cacheMap.AddOrUpdate(key, cache, (x, y) => cache);

            _logger?.LogInformation("[{0}] [subscribe] {1}", this._agent.GetName(), key);

            return cache;
        }

        public CacheData GetCache(string dataId, string group)
            => GetCache(dataId, group, TenantUtil.GetUserTenantForAcm());

        public CacheData GetCache(string dataId, string group, string tenant)
        {
            if (dataId == null || group == null) throw new ArgumentException();

            return _cacheMap.TryGetValue(GroupKey.GetKeyTenant(dataId, group, tenant), out var cache) ? cache : null;
        }

        internal void RemoveCache(string dataId, string group, string tenant = null)
        {
            string groupKey = tenant == null ? GroupKey.GetKey(dataId, group) : GroupKey.GetKeyTenant(dataId, group, tenant);

            _cacheMap.TryRemove(groupKey, out _);

            _logger?.LogInformation("[{0}] [unsubscribe] {1}", this._agent.GetName(), groupKey);
        }

        public async Task<bool> RemoveConfig(string dataId, string group, string tenant, string tag)
            => await _agent.RemoveConfigAsync(dataId, group, tenant, tag);

        public async Task<bool> PublishConfig(string dataId, string group, string tenant, string appName, string tag, string betaIps,
            string content, string type)
            => await _agent.PublishConfigAsync(dataId, group, tenant, appName, tag, betaIps, content, type);

        public Task<List<string>> GetServerConfig(string dataId, string group, string tenant, long readTimeout, bool notify)
        {
            if (group.IsNullOrWhiteSpace()) group = Constants.DEFAULT_GROUP;

            return this._agent.QueryConfigAsync(dataId, group, tenant, readTimeout, notify);
        }

        public string GetAgentName() => this._agent.GetName();

        internal bool IsHealthServer() => this._agent.GetIsHealthServer();

        public void Dispose()
        {
        }
    }
}
