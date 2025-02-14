﻿// --------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
// --------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Core;
using Microsoft.BridgeToKubernetes.Common;
using Microsoft.BridgeToKubernetes.Common.EndpointManager;
using Microsoft.BridgeToKubernetes.Common.EndpointManager.RequestArguments;
using Microsoft.BridgeToKubernetes.Common.Exceptions;
using Microsoft.BridgeToKubernetes.Common.IO;
using Microsoft.BridgeToKubernetes.Common.Json;
using Microsoft.BridgeToKubernetes.Common.Logging;
using Microsoft.BridgeToKubernetes.Common.Models;
using Microsoft.BridgeToKubernetes.Common.Models.LocalConnect;
using Microsoft.BridgeToKubernetes.Common.Socket;
using Microsoft.BridgeToKubernetes.Common.Utilities;
using Microsoft.BridgeToKubernetes.Library.ManagementClients;
using static Microsoft.BridgeToKubernetes.Common.Constants;

namespace Microsoft.BridgeToKubernetes.Library.EndpointManagement
{
    internal class EndpointManagementClient : ManagementClientBase, IEndpointManagementClient
    {
        private readonly Func<ISocket> _socketFactory;
        private readonly IProgress<ProgressUpdate> _progress;
        private readonly IFileSystem _fileSystem;
        private readonly IPlatform _platform;
        private readonly IAssemblyMetadataProvider _assemblyMetadataProvider;
        private readonly IEnvironmentVariables _environmentVariables;
        private readonly string _socketFilePath;
        private readonly TimeSpan _epmLaunchWaitTime = TimeSpan.FromSeconds(30);

        public delegate EndpointManagementClient Factory(string userAgent, string correlationId);

        public EndpointManagementClient(
            string userAgent,
            string correlationId,
            IOperationContext operationContext,
            Func<ISocket> socketFactory,
            IProgress<ProgressUpdate> progress,
            IFileSystem fileSystem,
            ILog log,
            IPlatform platform,
            IAssemblyMetadataProvider assemblyMetadataProvider,
            IEnvironmentVariables environmentVariables)
            : base(log, operationContext)
        {
            _progress = progress;
            _fileSystem = fileSystem;
            _platform = platform;
            _assemblyMetadataProvider = assemblyMetadataProvider;
            _environmentVariables = environmentVariables;
            _socketFilePath = _fileSystem.Path.Combine(fileSystem.GetPersistedFilesDirectory(DirectoryName.PersistedFiles), EndpointManager.ProcessName, EndpointManager.SocketName);
            _socketFactory = socketFactory;
            operationContext.UserAgent = userAgent;
            operationContext.CorrelationId = correlationId + LoggingConstants.CorrelationIdSeparator + LoggingUtils.NewId();
        }

        public Task AddHostsFileEntryAsync(string workloadNamespace, IEnumerable<HostsFileEntry> hostsFileEntries, CancellationToken cancellationToken)
        {
            var request = CreateRequest(EndpointManager.ApiNames.AddHostsFileEntry, new AddHostsFileEntryArgument { WorkloadNamespace = workloadNamespace, Entries = hostsFileEntries });
            return this.InvokeEndpointManagerAsync<EndpointManagerRequest<AddHostsFileEntryArgument>, EndpointManagerResult>(request, cancellationToken);
        }

        public async Task<IEnumerable<EndpointInfo>> AllocateIPAsync(IEnumerable<EndpointInfo> endpoints, CancellationToken cancellationToken)
        {
            var request = CreateRequest(EndpointManager.ApiNames.AllocateIP, new AllocateIPArgument { Endpoints = endpoints });
            var response = await this.InvokeEndpointManagerAsync<EndpointManagerRequest<AllocateIPArgument>, EndpointManagerResult<IEnumerable<EndpointInfo>>>(request, cancellationToken);
            return response?.Value;
        }

        public Task FreeIPAsync(IPAddress[] ipsToCollect, CancellationToken cancellationToken)
        {
            var request = CreateRequest(EndpointManager.ApiNames.FreeIP, new FreeIPArgument { IPAddresses = ipsToCollect });
            return this.InvokeEndpointManagerAsync<EndpointManagerRequest<FreeIPArgument>, EndpointManagerResult>(request, cancellationToken);
        }

        public async Task FreePortsAsync(IEnumerable<IElevationRequest> elevationRequests, CancellationToken cancellationToken)
        {
            IEnumerable<ProcessPortMapping> processPortMappings = elevationRequests.Where(request => request.RequestType == ElevationRequestType.FreePort)
                .Select(request => request as FreePortRequest)
                .SelectMany(freePortRequest => freePortRequest.OccupiedPorts)
                .Where(occupiedPort => occupiedPort.GetType() == typeof(ProcessPortMapping))
                .Select(mapping => mapping as ProcessPortMapping);

            if (processPortMappings.Any())
            {
                var request = CreateRequest(EndpointManager.ApiNames.KillProcess, new KillProcessArgument { ProcessPortMappings = processPortMappings });
                await this.InvokeEndpointManagerAsync<EndpointManagerRequest<KillProcessArgument>, EndpointManagerResult>(request, cancellationToken);
            }

            IEnumerable<ServicePortMapping> servicePortMappings = elevationRequests.Where(request => request.RequestType == ElevationRequestType.FreePort)
                                        .Select(request => request as FreePortRequest)
                                        .SelectMany(freePortRequest => freePortRequest.OccupiedPorts)
                                        .Where(occupiedPort => occupiedPort.GetType() == typeof(ServicePortMapping))
                                        .Select(mapping => mapping as ServicePortMapping);

            if (servicePortMappings.Any())
            {
                var request = CreateRequest(EndpointManager.ApiNames.DisableService, new DisableServiceArgument { ServicePortMappings = servicePortMappings });
                await this.InvokeEndpointManagerAsync<EndpointManagerRequest<DisableServiceArgument>, EndpointManagerResult>(request, cancellationToken);
            }
        }

        public async Task<bool> PingEndpointManagerAsync(CancellationToken cancellationToken)
        {
            EndpointManagerResult result = null;
            try
            {
                var request = CreateRequest(EndpointManager.ApiNames.Ping);
                result = await InvokeEndpointManagerAsync<EndpointManagerRequest, EndpointManagerResult>(request, cancellationToken, ensureEndpointManagerRunning: false);
            }
            catch (Exception ex) when (ex is IUserVisibleExceptionReporter)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Swallow all exceptions (unless it's UserVisible), since we just want to know if we can talk to the process or not.
                _log.ExceptionAsWarning(ex);
            }
            if (result == null)
            {
                return false;
            }

            return result.IsSuccess;
        }

        public async Task StartEndpointManagerAsync(CancellationToken cancellationToken)
        {
            // Note: this API needs to be separate from "Ping" because we want to ensure that the EPM is running.
            // Relying on Ping for this would result in an infinite loop.
            var request = CreateRequest(EndpointManager.ApiNames.Ping);
            await InvokeEndpointManagerAsync<EndpointManagerRequest, EndpointManagerResult>(request, cancellationToken);
        }

        public async Task<EndpointManagerSystemCheckMessage> SystemCheckAsync(CancellationToken cancellationToken)
        {
            var request = CreateRequest(EndpointManager.ApiNames.SystemCheck);
            var response = await this.InvokeEndpointManagerAsync<EndpointManagerRequest, EndpointManagerResult<EndpointManagerSystemCheckMessage>>(request, cancellationToken);
            var result = response?.Value ?? new EndpointManagerSystemCheckMessage();

            if (result.PortBinding == null)
            {
                result.PortBinding = new Dictionary<int, string>();
            }
            if (result.ServiceMessages == null)
            {
                result.ServiceMessages = new SystemServiceCheckMessage[] { };
            }
            return result;
        }

        public async Task StopEndpointManagerAsync(CancellationToken cancellationToken)
        {
            if (await this.PingEndpointManagerAsync(cancellationToken))
            {
                _log.Info($"Found running instance of the {EndpointManager.ProcessName}. Ordering shutdown.");
                await SendStopRequestAsync(cancellationToken);
                var result = await WebUtilities.RetryUntilTimeWithWaitAsync(async _ =>
                {
                    if (!await this.PingEndpointManagerAsync(cancellationToken))
                    {
                        _log.Info($"{EndpointManager.ProcessName} was successfully stopped.");
                        return true;
                    }

                    return false;
                }, maxWaitTime: TimeSpan.FromSeconds(12), waitInterval: TimeSpan.FromMilliseconds(500), cancellationToken);

                if (!result)
                {
                    var failedToCleanMessage = $"Failed to clean up {EndpointManager.ProcessName}.";
                    _log.Warning(failedToCleanMessage);
                    throw new InvalidOperationException(failedToCleanMessage);
                }
            }
            else
            {
                _log.Info($"Didn't find a running instance of {EndpointManager.ProcessName}.");
            }

            // If the previous instance wasn't running or was stopped but there is still a socket folder hanging around, let's clean it up.
            var socketFileDirectory = _fileSystem.Path.GetDirectoryName(_socketFilePath);
            var cleanupResult = await WebUtilities.RetryUntilTimeWithWaitAsync((i) => Task.FromResult(_fileSystem.EnsureDirectoryDeleted(socketFileDirectory, recursive: true, _log)),
                    maxWaitTime: TimeSpan.FromSeconds(4),
                    waitInterval: TimeSpan.FromSeconds(1),
                    cancellationToken);
            if (!cleanupResult)
            {
                // This can potentially have consequences on socket connectivity later on, but we don't throw because there is a chance the session will still succeed.
                // TODO(ansoedal): If this log shows up in telemetry frequently, need to take further action.
                _log.Error("Failed to clean up the previous socket folder.");
            }
        }

        #region private members

        private async Task SendStopRequestAsync(CancellationToken cancellationToken)
        {
            try
            {
                var request = CreateRequest(EndpointManager.ApiNames.Stop);
                await this.InvokeEndpointManagerAsync<EndpointManagerRequest, EndpointManagerResult>(request, cancellationToken, ensureEndpointManagerRunning: false);
            }
            catch (Exception e)
            {
                this._log.ExceptionAsWarning(e);
            }
        }

        private EndpointManagerRequest CreateRequest(EndpointManager.ApiNames apiName)
            => new EndpointManagerRequest()
            {
                ApiName = apiName.ToString(),
                CorrelationId = _operationContext.CorrelationId
            };

         private EndpointManagerRequest<T> CreateRequest<T>(EndpointManager.ApiNames apiName, T argument) where T : EndpointManagerRequestArgument
             => new EndpointManagerRequest<T>()
            {
                ApiName = apiName.ToString(),
                CorrelationId = _operationContext.CorrelationId,
                Argument = argument
            };

        private async Task<ResponseType> InvokeEndpointManagerAsync<RequestType, ResponseType>(RequestType request, CancellationToken cancellationToken, bool ensureEndpointManagerRunning = true)
            where RequestType : EndpointManagerRequest
            where ResponseType : EndpointManagerResult
        {
            if (ensureEndpointManagerRunning)
            {
                // We do not want invoke this method if we are calling from the 'Ping' method, as that will result in an infinite loop
                await EnsureEndpointManagerRunningAsync(cancellationToken);
            }

            var errorMessage = $"Could not connect to {EndpointManager.ProcessName}";
            ResponseType deserializedResult = null;

            try
            {
                using (var socket = _socketFactory.Invoke())
                using (cancellationToken.Register(() => socket.Close()))
                {
                    _log.Info($"Connecting to {EndpointManager.ProcessName}");

                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketFilePath));

                    // Wait for the EndpointManager handshake.
                    var result = await socket.ReadUntilEndMarkerAsync();
                    if (!result.Equals(EndpointManager.SocketHandshake))
                    {
                        throw new UnexpectedStateException($"Received unexpected message over socket: '{result}'", _log);
                    }

                    // Send api invocation request
                    var serializedRequest = JsonHelpers.SerializeObject(request);
                    _log.Info($"Sending request: '{serializedRequest}'");
                    await socket.SendWithEndMarkerAsync(serializedRequest);

                    // Wait for response
                    result = await socket.ReadUntilEndMarkerAsync();
                    _log.Info($"Received response: '{result}'");
                    deserializedResult = JsonHelpers.DeserializeObject<ResponseType>(result);
                }
            }
            catch (Exception ex) when (ex is SocketException socketException && (socketException.SocketErrorCode == SocketError.ConnectionRefused
                                                                                || socketException.SocketErrorCode == SocketError.AddressNotAvailable
                                                                                || socketException.SocketErrorCode == SocketError.NetworkDown))
            {
                // We expect to hit this case if the endpoint manager is not yet running.
                _log.Info($"{EndpointManager.ProcessName} is not running: '{ex.Message}'");
            }
            catch (Exception ex) when (ex is IUserVisibleExceptionReporter)
            {
                throw;
            }
            catch (Exception ex) when (ex.InnerException is DependencyResolutionException autofacEx
                && autofacEx.InnerException is SocketException socketEx
                && socketEx.SocketErrorCode == SocketError.AddressFamilyNotSupported)
            {
                throw new UserVisibleException(_operationContext, Resources.UnixDomainSocketsNotSupported, Product.Name);
            }
            catch (UnexpectedStateException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Exception(ex);
                throw new InvalidOperationException(errorMessage, ex);
            }

            if (deserializedResult != null && !deserializedResult.IsSuccess)
            {
                // Operation failed, but we got back a well-formed response. We check for user-visible error types
                if (Enum.TryParse(deserializedResult.ErrorType, out EndpointManager.Errors errorType) && errorType == EndpointManager.Errors.UserVisible)

                {
                    throw new UserVisibleException(_log.OperationContext, deserializedResult.ErrorMessage);
                }
                else
                {
                    throw new InvalidOperationException(deserializedResult.ErrorMessage);
                }
            }
            return deserializedResult;
        }

        private async Task EnsureEndpointManagerRunningAsync(CancellationToken cancellationToken)
        {
            if (await this.IsCurrentEndpointManagerRunning(cancellationToken))
            {
                return;
            }

            var logFileDirectory = _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), DirectoryName.Logs);

            // Try to acquire the Mutex for launching the endpoint manager
            using (var mutex = new Mutex(initiallyOwned: false, name: @"Global\BridgeEndpointManagerLaunch"))
            {
                try
                {
                    var stopWatch = new Stopwatch();
                    stopWatch.Start();
                    while (stopWatch.Elapsed < _epmLaunchWaitTime)
                    {
                        _log.Verbose("Trying to acquire mutex...");
                        bool acquiredMutex = false;
                        try
                        {
                            // TimeSpan.Zero used to test the mutex's signal state and return immediately without blocking
                            acquiredMutex = mutex.WaitOne(TimeSpan.Zero);
                        }
                        catch (AbandonedMutexException ex)
                        {
                            // Another instance of bridge exited without releasing the mutex. We continue and take over.
                            _log.ExceptionAsWarning(ex);
                            break;
                        }

                        if (acquiredMutex)
                        {
                            _log.Verbose($"Acquired mutex. Proceeding to launch {nameof(EndpointManager)}.");
                            break;
                        }
                        _log.Verbose($"Another instance is open. Checking if {nameof(EndpointManager)} will come up...");

                        // Adding a timeout here. We have seen socket exceptions/connection refused in the past when we ping without any wait time.
                        await Task.Delay(TimeSpan.FromMilliseconds(200));
                        if (await this.IsCurrentEndpointManagerRunning(cancellationToken))
                        {
                            _log.Verbose($"Current endpoint manager is running. Returning.");
                            return;
                        }
                    }

                    // Determine the current user
                    (var exitCode, var currentUserName) = await _platform.DetermineCurrentUserWithRetriesAsync(cancellationToken);
                    var resultMessage = $"{nameof(_platform.DetermineCurrentUserWithRetriesAsync)} returned exit code {exitCode}";
                    if (exitCode != 0 || string.IsNullOrWhiteSpace(currentUserName))
                    {
                        _log.Error(resultMessage);
                        throw new UserVisibleException(_operationContext, Resources.FailedToDetermineCurrentUser);
                    }
                    _log.Verbose(resultMessage);

                    var executableName = $"{EndpointManager.ProcessName}{(_platform.IsWindows ? ".exe" : string.Empty)}";
                    var launcherPath = _fileSystem.Path.Combine(_fileSystem.Path.GetExecutingAssemblyDirectoryPath(), EndpointManager.DirectoryName, executableName);
                    if (!_fileSystem.FileExists(launcherPath))
                    {
                        throw new InvalidOperationException($"Failed to find '{launcherPath}'.");
                    }

                    if (_platform.IsWindows)
                    {
                        // Since EndpointManager is started as an elevated process and when a DOTNET_ROOT env variable is set,
                        // on windows it is not possible to pass the DOTNET_ROOT value, so using EndpointManagerLauncher to start EndpointManager.
                        // When VSCode uses BinariesV2 strategy to download clients, DOTNET_ROOT env var is set.
                        if (!string.IsNullOrEmpty(this._environmentVariables.DotNetRoot))
                        {
                            var epmLauncherPath = _fileSystem.Path.Combine(_fileSystem.Path.GetExecutingAssemblyDirectoryPath(), EndpointManager.LauncherDirectoryName, executableName);
                            var quotedArguments = $"\"{this._environmentVariables.DotNetRoot.Trim('"')}\" \"{launcherPath}\" \"{currentUserName}\" \"{_socketFilePath}\" \"{logFileDirectory}\" \"{_operationContext.CorrelationId}\"";
                            // Note: If the UAC prompt is declined, the process will throw a Win32Exception.
                            Process.Start(this.GetEndpointManagerLaunchInfoWindows($"\"{epmLauncherPath}\"", quotedArguments));
                        }
                        else
                        {
                            var quotedArguments = $"\"{currentUserName}\" \"{_socketFilePath}\" \"{logFileDirectory}\" \"{_operationContext.CorrelationId}\"";
                            // Note: If the UAC prompt is declined, the process will throw a Win32Exception.
                            Process.Start(this.GetEndpointManagerLaunchInfoWindows($"\"{launcherPath}\"", quotedArguments));
                        }

                        await CheckEndpointManagerAliveAsync(cancellationToken);
                        return;
                    }

                    var fileName = string.Empty;
                    var command = string.Empty;
                    if (_platform.IsOSX)
                    {
                        var quotedLaunchPathAndArguments = $"\\\\\\\"{launcherPath}\\\\\\\" \\\\\\\"{currentUserName}\\\\\\\" \\\\\\\"{_socketFilePath}\\\\\\\" \\\\\\\"{logFileDirectory}\\\\\\\" \\\\\\\"{_operationContext.CorrelationId}\\\\\\\"";

                        // We launch the EPM using AppleScript when on OSX.
                        fileName = "/usr/bin/osascript";

                        // Tell the shell to redirect output so that this process exits and the EPM continues to run in the background
                        // For more info: https://developer.apple.com/library/archive/technotes/tn2065/_index.html#//apple_ref/doc/uid/DTS10003093-CH1-TNTAG5-I_WANT_TO_START_A_BACKGROUND_SERVER_PROCESS__HOW_DO_I_MAKE_DO_SHELL_SCRIPT_NOT_WAIT_UNTIL_THE_COMMAND_COMPLETES_
                        StringBuilder commandBuilder = new StringBuilder();
                        commandBuilder.Append("-e \"do shell script ");
                        commandBuilder.Append($"\\\"{quotedLaunchPathAndArguments} &> /dev/null &\\\"");
                        commandBuilder.Append($" with prompt \\\"{Product.Name} wants to launch {EndpointManager.ProcessName}.\\\" with administrator privileges\"");
                        command = commandBuilder.ToString();

                        _log.Info($"Launch {EndpointManager.ProcessName}: {fileName} {command}");
                    }
                    else
                    {
                        var quotedLaunchPathAndArguments = $"\\\"{launcherPath}\\\" \\\"{currentUserName}\\\" \\\"{_socketFilePath}\\\" \\\"{logFileDirectory}\\\" \\\"{_operationContext.CorrelationId}\\\"";

                        // Try to use pkexec to show a GUI prompt for the user's password so we can run EndpointManager as root.
                        // If we are already running as root, then pkexec will not show a prompt.
                        // When running in Codespaces the user is setup to run sudo without needing to enter password.
                        fileName = _environmentVariables.IsCodespaces ? "sudo" : "pkexec";
                        command = $"env HOME=\"{_fileSystem.HomeDirectoryPath}\" bash -c \"{quotedLaunchPathAndArguments} &> /dev/null &\"";
                        _log.Info($"Launch {EndpointManager.ProcessName}: {fileName} {command}");
                    }

                    Dictionary<string, string> envVars = null;
                    if (!string.IsNullOrEmpty(_environmentVariables.DotNetRoot))
                    {
                        _log.Info("Setting the '{0}' environment variable with '{1}' while launching '{2}'.", EnvironmentVariables.Names.DotNetRoot, _environmentVariables.DotNetRoot, EndpointManager.ProcessName);
                        envVars = new Dictionary<string, string>() { { EnvironmentVariables.Names.DotNetRoot, _environmentVariables.DotNetRoot } };
                    }

                    var launchExitCode = _platform.Execute(executable: fileName,
                                                command: command,
                                                logCallback: (line) => _log.Info($"Launch output: {line}"),
                                                envVariables: envVars,
                                                timeout: TimeSpan.FromSeconds(120),
                                                cancellationToken: cancellationToken,
                                                out string outPut);

                    // Wait until user has entered their credentials to start pinging
                    if (launchExitCode == 0)
                    {
                        await CheckEndpointManagerAliveAsync(cancellationToken);
                        return;
                    }
                    if ((_platform.IsOSX && launchExitCode == 1) || (_platform.IsLinux && launchExitCode == 126))
                    {
                        // User cancellation
                        throw new InvalidUsageException(_log.OperationContext, Resources.LaunchProcessCancelled, EndpointManager.ProcessName);
                    }

                    // Something went wrong.
                    throw new InvalidOperationException($"{EndpointManager.ProcessName} exited with exit code {launchExitCode}");
                }
                catch (Exception ex) when (ex is IUserVisibleExceptionReporter)
                {
                    // Always bubble up UserVisible exceptions
                    throw;
                }
                catch (Exception ex) when (ex is Win32Exception)
                {
                    // This catch block will be hit if the user declines the UAC prompt on Windows.
                    // However, since potential other Win32Exceptions could arise, we log the exception as a warning.
                    _log.ExceptionAsWarning(ex);
                    throw new InvalidUsageException(_log.OperationContext, ex, Resources.LaunchProcessCancelled, EndpointManager.ProcessName);
                }
                catch (Exception ex)
                {
                    _log.Exception(ex);
                    throw new InvalidOperationException(string.Format(Resources.FailedToLaunchEndpointManagerFormat, EndpointManager.ProcessName));
                }
                finally
                {
                    _log.Info("Releasing mutex if owned...");
                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
        }

        private async Task CheckEndpointManagerAliveAsync(CancellationToken cancellationToken)
        {
            this.ReportProgress(Resources.WaitingForEndpointManagerFormat, EndpointManager.ProcessName);
            var result = await WebUtilities.RetryUntilTimeWithWaitAsync(async (i) => await this.PingEndpointManagerAsync(cancellationToken),
                                                        maxWaitTime: _epmLaunchWaitTime,
                                                        waitInterval: TimeSpan.FromMilliseconds(500),
                                                        cancellationToken);

            if (result)
            {
                ReportProgress($"{EndpointManager.ProcessName} came up successfully.");
                return;
            }

            throw new InvalidOperationException(string.Format(Resources.FailedToLaunchEndpointManagerFormat, EndpointManager.ProcessName));
        }

        private ProcessStartInfo GetEndpointManagerLaunchInfoWindows(string launcherPath, string quotedArguments)
        {
            this._log.Trace(EventLevel.Informational, $"Launcher path: {launcherPath} quotedArguments: {quotedArguments}");
            // Start launcher as admin
            ProcessStartInfo psi = new ProcessStartInfo()
            {
                FileName = launcherPath,
                Arguments = quotedArguments,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Verb = "runas"
            };

            return psi;
        }

        /// <summary>
        /// Progress reporter for <see cref="EndpointManagementClient"/>
        /// </summary>
        private void ReportProgress(string message, params object[] args)
        {
            this._progress.Report(new ProgressUpdate(0, ProgressStatus.EndpointManagementClient, new ProgressMessage(EventLevel.Informational, _log.SaferFormat(message, args))));
        }

        /// <summary>
        /// Checks if the Endpoint Manager with the same version of calling assembly is running
        /// </summary>
        /// <returns>True if Endpoint Manager is running with the same version, false otherwise</returns>
        private async Task<bool> IsCurrentEndpointManagerRunning(CancellationToken cancellationToken)
        {
            EndpointManagerResult<string> result = null;

            try
            {
                var request = CreateRequest(EndpointManager.ApiNames.Version);
                result = await this.InvokeEndpointManagerAsync<EndpointManagerRequest, EndpointManagerResult<string>>(request, cancellationToken, ensureEndpointManagerRunning: false);
            }
            catch (Exception e)
            {
                this._log.ExceptionAsWarning(e);
                return false;
            }

            if (result == null || !result.IsSuccess)
            {
                this._log.Info($"{EndpointManager.ProcessName} is not running.");
                return false;
            }

            var expectedVersion = this._assemblyMetadataProvider.AssemblyVersion;

            if (string.IsNullOrEmpty(result.Value))
            {
                this.SendStopRequestAsync(cancellationToken).Forget();
                throw new InvalidOperationException($"Found invalid version of {EndpointManager.ProcessName}. Expected version: '{expectedVersion}'");
            }

            var versionArray = result.Value.Split('.');

            // Handle local builds where third part of the version is 0.
            if (versionArray[2].Equals("0"))
            {
                this._log.Info($"Accepting the local version '{result.Value}' of {EndpointManager.ProcessName}.");
                return true;
            }

            if (result.Value.CompareTo(expectedVersion) != 0)
            {
                this._log.Info($"Found {EndpointManager.ProcessName} version '{result.Value}' which is not equal to expected version '{expectedVersion}'");
                await StopEndpointManagerAsync(cancellationToken);
                return false;
            }

            this._log.Info($"Found {EndpointManager.ProcessName} version '{result.Value}' which is equal to expected version '{expectedVersion}'");
            return true;
        }

        #endregion private members
    }
}