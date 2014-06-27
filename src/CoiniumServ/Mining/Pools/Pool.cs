﻿#region License
// 
//     CoiniumServ - Crypto Currency Mining Pool Server Software
//     Copyright (C) 2013 - 2014, CoiniumServ Project - http://www.coinium.org
//     https://github.com/CoiniumServ/CoiniumServ
// 
//     This software is dual-licensed: you can redistribute it and/or modify
//     it under the terms of the GNU General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// 
//     This program is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU General Public License for more details.
//    
//     For the terms of this license, see licenses/gpl_v3.txt.
// 
//     Alternatively, you can license this software under a commercial
//     license or white-label it as set out in licenses/commercial.txt.
// 
#endregion
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using Coinium.Crypto.Algorithms;
using Coinium.Daemon;
using Coinium.Mining.Jobs;
using Coinium.Mining.Miners;
using Coinium.Mining.Pools.Config;
using Coinium.Mining.Shares;
using Coinium.Persistance;
using Coinium.Server;
using Coinium.Services.Rpc;
using Coinium.Utils.Configuration;
using Coinium.Utils.Helpers.Validation;
using Serilog;

namespace Coinium.Mining.Pools
{
    /// <summary>
    /// Contains pool services and server.
    /// </summary>
    public class Pool : IPool
    {
        public IPoolConfig Config { get; private set; }

        private readonly IDaemonClient _daemonClient;
        private readonly IServerFactory _serverFactory;
        private readonly IServiceFactory _serviceFactory;
        private readonly IJobManagerFactory _jobManagerFactory;
        private readonly IShareManagerFactory _shareManagerFactory;
        private readonly IMinerManagerFactory _minerManagerFactory;
        private readonly IHashAlgorithmFactory _hashAlgorithmFactory;
        private readonly IStorageFactory _storageManagerFactory;
        private readonly IGlobalConfigFactory _globalConfigFactory;
        private IMinerManager _minerManager;
        private IJobManager _jobManager;
        private IShareManager _shareManager;
        private IStorage _storageManager;

        private Dictionary<IMiningServer, IRpcService> _servers;

        /// <summary>
        /// Instance id of the pool.
        /// </summary>
        public UInt32 InstanceId { get; private set; }

        private Timer _timer;

        /// <summary>
        /// Initializes a new instance of the <see cref="Pool" /> class.
        /// </summary>
        /// <param name="hashAlgorithmFactory">The hash algorithm factory.</param>
        /// <param name="serverFactory">The server factory.</param>
        /// <param name="serviceFactory">The service factory.</param>
        /// <param name="client">The client.</param>
        /// <param name="minerManagerFactory">The miner manager factory.</param>
        /// <param name="jobManagerFactory">The job manager factory.</param>
        /// <param name="shareManagerFactory">The share manager factory.</param>
        /// <param name="storageManagerFactory"></param>
        public Pool(
            IHashAlgorithmFactory hashAlgorithmFactory, 
            IServerFactory serverFactory, 
            IServiceFactory serviceFactory,
            IDaemonClient client, 
            IMinerManagerFactory minerManagerFactory, 
            IJobManagerFactory jobManagerFactory, 
            IShareManagerFactory shareManagerFactory,
            IStorageFactory storageManagerFactory,
            IGlobalConfigFactory globalConfigFactory)
        {
            Enforce.ArgumentNotNull(hashAlgorithmFactory, "IHashAlgorithmFactory");
            Enforce.ArgumentNotNull(serverFactory, "IServerFactory");
            Enforce.ArgumentNotNull(serviceFactory, "IServiceFactory");
            Enforce.ArgumentNotNull(client, "IDaemonClient");
            Enforce.ArgumentNotNull(minerManagerFactory, "IMinerManagerFactory");
            Enforce.ArgumentNotNull(jobManagerFactory, "IJobManagerFactory");
            Enforce.ArgumentNotNull(shareManagerFactory, "IShareManagerFactory");
            Enforce.ArgumentNotNull(storageManagerFactory, "IStorageFactory");
            Enforce.ArgumentNotNull(globalConfigFactory, "IGlobalConfigFactory");

            _daemonClient = client;
            _minerManagerFactory = minerManagerFactory;
            _jobManagerFactory = jobManagerFactory;
            _shareManagerFactory = shareManagerFactory;
            _serverFactory = serverFactory;
            _serviceFactory = serviceFactory;
            _hashAlgorithmFactory = hashAlgorithmFactory;
            _storageManagerFactory = storageManagerFactory;
            _globalConfigFactory = globalConfigFactory;

            GenerateInstanceId();
        }

        /// <summary>
        /// Initializes the specified bind ip.
        /// </summary>
        /// <param name="config">The configuration.</param>
        /// <exception cref="System.ArgumentNullException">config;config.Daemon can not be null!</exception>
        public void Initialize(IPoolConfig config)
        {
            Config = config;

            // init coin daemon.
            InitDaemon();

            // init managers.
            InitManagers();

            // init servers
            InitServers();

            // other stuff
            _timer = new Timer(Timer, null, TimeSpan.Zero, new TimeSpan(0, 0, 0, 10)); // setup a timer to broadcast jobs.
        }

        private void InitManagers()
        {
            _storageManager = _storageManagerFactory.Get(Storages.Redis);

            _minerManager = _minerManagerFactory.Get(_daemonClient);

            _jobManager = _jobManagerFactory.Get(_daemonClient, _minerManager, _hashAlgorithmFactory.Get(Config.Coin.Algorithm));
            _jobManager.Initialize(InstanceId);

            _shareManager = _shareManagerFactory.Get(_daemonClient, _jobManager, _storageManager);
        }

        private void InitDaemon()
        {
            if (Config.Daemon == null || Config.Daemon.Valid == false)
                Log.ForContext<Pool>().Error("Coin daemon configuration is not valid!");

            _daemonClient.Initialize(Config.Daemon);
        }

        private void InitServers()
        {
            _servers = new Dictionary<IMiningServer, IRpcService>();

            // we don't need here a server config list as a pool can host only one instance of stratum and one vanilla server.
            // we must be dictative here, using a server list may cause situations we don't want (multiple stratum configs etc..)
            if (Config.Stratum != null)
            {
                var stratumServer = _serverFactory.Get("Stratum", _minerManager);
                var stratumService = _serviceFactory.Get("Stratum", _jobManager, _shareManager, _daemonClient);
                stratumServer.Initialize(Config.Stratum);

                _servers.Add(stratumServer, stratumService);
            }

            if (Config.Vanilla != null)
            {
                var vanillaServer = _serverFactory.Get("Vanilla", _minerManager);
                var vanillaService = _serviceFactory.Get("Vanilla", _jobManager, _shareManager, _daemonClient);

                vanillaServer.Initialize(Config.Vanilla);

                _servers.Add(vanillaServer, vanillaService);
            }
        }

        public void Start()
        {
            if (!Config.Valid)
            {
                Log.ForContext<Pool>().Error("Can't start pool as configuration is not valid.");
                return;
            }                

            foreach (var server in _servers)
            {
                server.Key.Start();
            }
        }

        public void Stop()
        {
            throw new NotImplementedException();
        }
        private void Timer(object state)
        {
            _jobManager.Broadcast();
        }

        /// <summary>
        /// Generates an instance Id for the pool that is cryptographically random. 
        /// </summary>
        private void GenerateInstanceId()
        {
            var rndGenerator = RandomNumberGenerator.Create(); // cryptographically random generator.
            var randomBytes = new byte[4];
            rndGenerator.GetNonZeroBytes(randomBytes); // create cryptographically random array of bytes.
            InstanceId = BitConverter.ToUInt32(randomBytes, 0); // convert them to instance Id.
            Log.ForContext<Pool>().Debug("Generated cryptographically random instance Id: {0}", InstanceId);
        }
    }
}