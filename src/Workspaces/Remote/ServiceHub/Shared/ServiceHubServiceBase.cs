﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Options;
using Roslyn.Utilities;
using StreamJsonRpc;

namespace Microsoft.CodeAnalysis.Remote
{
    // TODO: all service hub service should be extract to interface so that it can support multiple hosts.
    //       right now, tightly coupled to service hub
    internal abstract class ServiceHubServiceBase : IDisposable
    {
        private static int s_instanceId;

        private readonly CancellationTokenSource _cancellationTokenSource;

        protected readonly int InstanceId;

        protected readonly JsonRpc Rpc;
        protected readonly TraceSource Logger;
        protected readonly AssetStorage AssetStorage;
        protected readonly CancellationToken CancellationToken;

        private int _sessionId;
        private Checksum _solutionChecksumOpt;
        private RoslynServices _lazyRoslynServices;

        [Obsolete("For backward compatibility. this will be removed once all callers moved to new ctor")]
        protected ServiceHubServiceBase(Stream stream, IServiceProvider serviceProvider)
        {
            InstanceId = Interlocked.Add(ref s_instanceId, 1);

            // in unit test, service provider will return asset storage, otherwise, use the default one
            AssetStorage = (AssetStorage)serviceProvider.GetService(typeof(AssetStorage)) ?? AssetStorage.Default;

            Logger = (TraceSource)serviceProvider.GetService(typeof(TraceSource));
            Logger.TraceInformation($"{DebugInstanceString} Service instance created");

            _cancellationTokenSource = new CancellationTokenSource();
            CancellationToken = _cancellationTokenSource.Token;

            Rpc = JsonRpc.Attach(stream, this);
            Rpc.JsonSerializer.Converters.Add(AggregateJsonConverter.Instance);

            Rpc.Disconnected += OnRpcDisconnected;
        }

        protected ServiceHubServiceBase(IServiceProvider serviceProvider, Stream stream)
        {
            InstanceId = Interlocked.Add(ref s_instanceId, 1);

            // in unit test, service provider will return asset storage, otherwise, use the default one
            AssetStorage = (AssetStorage)serviceProvider.GetService(typeof(AssetStorage)) ?? AssetStorage.Default;

            Logger = (TraceSource)serviceProvider.GetService(typeof(TraceSource));
            Logger.TraceInformation($"{DebugInstanceString} Service instance created");

            _cancellationTokenSource = new CancellationTokenSource();
            CancellationToken = _cancellationTokenSource.Token;

            // due to this issue - https://github.com/dotnet/roslyn/issues/16900#issuecomment-277378950
            // all sub type must explicitly start JsonRpc once everything is
            // setup
            Rpc = new JsonRpc(stream, stream, this);
            Rpc.JsonSerializer.Converters.Add(AggregateJsonConverter.Instance);
            Rpc.Disconnected += OnRpcDisconnected;
        }

        protected string DebugInstanceString => $"{GetType()} ({InstanceId})";

        protected RoslynServices RoslynServices
        {
            get
            {
                if (_lazyRoslynServices == null)
                {
                    _lazyRoslynServices = new RoslynServices(_sessionId, AssetStorage);
                }

                return _lazyRoslynServices;
            }
        }

        protected Task<Solution> GetSolutionAsync()
        {
            Contract.ThrowIfNull(_solutionChecksumOpt);
            return RoslynServices.SolutionService.GetSolutionAsync(_solutionChecksumOpt, CancellationToken);
        }

        protected Task<Solution> GetSolutionWithSpecificOptionsAsync(OptionSet options)
        {
            Contract.ThrowIfNull(_solutionChecksumOpt);
            return RoslynServices.SolutionService.GetSolutionAsync(_solutionChecksumOpt, options, CancellationToken);
        }

        protected virtual void Dispose(bool disposing)
        {
            // do nothing here
        }

        protected void LogError(string message)
        {
            Logger.TraceEvent(TraceEventType.Error, 0, $"{DebugInstanceString} : " + message);
        }

        public virtual void Initialize(int sessionId, byte[] solutionChecksum)
        {
            // set session related information
            _sessionId = sessionId;

            if (solutionChecksum != null)
            {
                _solutionChecksumOpt = new Checksum(solutionChecksum);
            }
        }

        public void Dispose()
        {
            Rpc.Dispose();
            Dispose(false);

            Logger.TraceInformation($"{DebugInstanceString} Service instance disposed");
        }

        protected virtual void OnDisconnected(JsonRpcDisconnectedEventArgs e)
        {
            // do nothing
        }

        private void OnRpcDisconnected(object sender, JsonRpcDisconnectedEventArgs e)
        {
            // raise cancellation
            _cancellationTokenSource.Cancel();

            OnDisconnected(e);

            if (e.Reason != DisconnectedReason.Disposed)
            {
                LogError($"Client stream disconnected unexpectedly: {e.Exception?.GetType().Name} {e.Exception?.Message}");
            }
        }
    }
}