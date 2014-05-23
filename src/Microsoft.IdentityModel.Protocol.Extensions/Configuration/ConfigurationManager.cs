﻿//-----------------------------------------------------------------------
// Copyright (c) Microsoft Open Technologies, Inc.
// All Rights Reserved
// Apache License 2.0
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//-----------------------------------------------------------------------

using System;
using System.Diagnostics.Contracts;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.IdentityModel.Protocols
{
    public class ConfigurationManager<T> : IConfigurationManager<T>
    {
        public static readonly TimeSpan DefaultAutomaticRefreshInterval = new TimeSpan(5, 0, 0, 0);
        public static readonly TimeSpan DefaultDelayBetweenRefreshAttempts = new TimeSpan(0, 0, 0, 30);
        public static readonly TimeSpan MinimumAutomaticRefreshInterval = new TimeSpan(0, 0, 10, 0);
        public static readonly TimeSpan AbsoluteMinimumDelayBetweenRefreshAttempts = new TimeSpan(0, 0, 0, 1);

        private TimeSpan _automaticRefreshInterval = DefaultAutomaticRefreshInterval;
        private TimeSpan _minimumDelayBetweenRefreshAttempts = DefaultDelayBetweenRefreshAttempts;
        private DateTimeOffset _syncAfter = new DateTimeOffset(new DateTime(2001, 1, 1));
        private DateTimeOffset _lastRefresh = new DateTimeOffset(new DateTime(2001, 1, 1));

        private readonly SemaphoreSlim _refreshLock;
        private readonly string _metadataAddress;
        private readonly IDocumentRetriever _retriever;
        private readonly IConfigurationRetriever<T> _reader;
        private T _currentConfiguration;

        public ConfigurationManager(string metadataAddress)
            : this(metadataAddress, new GenericDocumentRetriever())
        {
        }

        public ConfigurationManager(string metadataAddress, HttpClient httpClient)
            : this(metadataAddress, new HttpDocumentRetriever(httpClient))
        {
        }

        public ConfigurationManager(string metadataAddress, IDocumentRetriever retriever)
            : this(metadataAddress, retriever, GetConfigurationReader())
        {
        }

        public ConfigurationManager(string metadataAddress, IDocumentRetriever retriever, IConfigurationRetriever<T> reader)
        {
            if (string.IsNullOrWhiteSpace(metadataAddress))
            {
                throw new ArgumentNullException("metadataAddress");
            }
            if (retriever == null)
            {
                throw new ArgumentNullException("retriever");
            }
            if (reader == null)
            {
                throw new ArgumentNullException("reader");
            }
            _metadataAddress = metadataAddress;
            _retriever = retriever;
            _reader = reader;
            _refreshLock = new SemaphoreSlim(1);
        }

        /// <summary>
        /// How often should an automatic metadata refresh be attempted
        /// </summary>
        public TimeSpan AutomaticRefreshInterval
        {
            get { return _automaticRefreshInterval; }
            set
            {
                if (value < MinimumAutomaticRefreshInterval)
                {
                    throw new ArgumentOutOfRangeException("value", value, MinimumAutomaticRefreshInterval.ToString());
                }
                _automaticRefreshInterval = value;
            }
        }

        /// <summary>
        /// The minimum time between retrievals, in the event that a retrieval failed, or that a refresh was explicitly requested.
        /// </summary>
        public TimeSpan MinimumIntervalBetweenRefreshAttempts
        {
            get { return _minimumDelayBetweenRefreshAttempts; }
            set
            {
                if (value < AbsoluteMinimumDelayBetweenRefreshAttempts)
                {
                    throw new ArgumentOutOfRangeException("value", value, AbsoluteMinimumDelayBetweenRefreshAttempts.ToString());
                }
                _minimumDelayBetweenRefreshAttempts = value;
            }
        }

        private static IConfigurationRetriever<T> GetConfigurationReader()
        {
            if (typeof(T).Equals(typeof(WsFederationConfiguration)))
            {
                return (IConfigurationRetriever<T>)new WsFederationConfigurationRetriever();
            }
            if (typeof(T).Equals(typeof(OpenIdConnectConfiguration)))
            {
                return (IConfigurationRetriever<T>)new OpenIdConnectConfigurationRetriever();
            }
            throw new NotImplementedException(typeof(T).FullName);
        }

        public async Task<T> GetConfigurationAsync(CancellationToken cancel)
        {
            await _refreshLock.WaitAsync(cancel);
            try
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                Exception retrieveEx = null;
                if (_syncAfter < now)
                {
                    try
                    {
                        _currentConfiguration = await _reader.GetConfigurationAysnc(_retriever, _metadataAddress, cancel);
                        Contract.Assert(_currentConfiguration != null);
                        _lastRefresh = now;
                        _syncAfter = now + _automaticRefreshInterval;
                    }
                    catch (Exception ex)
                    {
                        // TODO: Log
                        retrieveEx = ex;
                        _syncAfter = now + _minimumDelayBetweenRefreshAttempts;
                    }
                }

                if (_currentConfiguration == null)
                {
                    throw new Exception("Configuration unavailable", retrieveEx);
                }
                // Stale metadata is better than no metadata
                return _currentConfiguration;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        public void RequestRefresh()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (_lastRefresh + _minimumDelayBetweenRefreshAttempts > now)
            {
                _syncAfter = now;
            }
        }
    }
}