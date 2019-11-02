﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.MixedReality.SpatialAlignment;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Microsoft.MixedReality.SpectatorView
{
    public class SpatialCoordinateSystemManager : Singleton<SpatialCoordinateSystemManager>
    {
        /// <summary>
        /// Check for debug logging.
        /// </summary>
        [Tooltip("Check for debug logging.")]
        [SerializeField]
        private bool debugLogging = false;

        /// <summary>
        /// Check to show debug visuals.
        /// </summary>
        [Tooltip("Check to show debug visuals.")]
        public bool showDebugVisuals = false;

        /// <summary>
        /// Game Object to render at spatial coordinate locations when showing debug visuals.
        /// </summary>
        [Tooltip("Game Object to render at spatial coordinate locations when showing debug visuals.")]
        public GameObject debugVisual = null;

        /// <summary>
        /// Debug visual scale.
        /// </summary>
        [Tooltip("Debug visual scale.")]
        public float debugVisualScale = 1.0f;

        public event Action<SpatialCoordinateSystemParticipant> ParticipantConnected;

        public event Action<SpatialCoordinateSystemParticipant> ParticipantDisconnected;

        internal const string CoordinateStateMessageHeader = "COORDSTATE";
        internal const string SupportedLocalizersMessageHeader = "SUPPORTLOC";
        private const string LocalizeCommand = "LOCALIZE";
        private const string LocalizationCompleteCommand = "LOCALIZEDONE";
        private readonly Dictionary<Guid, ISpatialLocalizer> localizers = new Dictionary<Guid, ISpatialLocalizer>();
        private readonly Dictionary<SocketEndpoint, TaskCompletionSource<bool>> remoteLocalizationSessions = new Dictionary<SocketEndpoint, TaskCompletionSource<bool>>();
        private Dictionary<SocketEndpoint, SpatialCoordinateSystemParticipant> participants = new Dictionary<SocketEndpoint, SpatialCoordinateSystemParticipant>();
        private HashSet<INetworkManager> networkManagers = new HashSet<INetworkManager>();
        private ITrackingObserver trackingObserver = null;

        private readonly object localizationLock = new object();
        private LocalizationSessionDetails currentLocalizationSession = null;

        private class LocalizationSessionDetails
        {
            public ISpatialLocalizationSession Session { get; private set; }
            public TaskCompletionSource<bool> CompletionSource { get; private set; }
            public SpatialCoordinateSystemParticipant Participant { get; private set; }

            public LocalizationSessionDetails(ISpatialLocalizationSession session, SpatialCoordinateSystemParticipant participant)
            {
                this.Session = session;
                this.Participant = participant;
                this.CompletionSource = new TaskCompletionSource<bool>();
            }
        }

        /// <summary>
        /// Current Tracking state for the AR/VR Device associated with the application.
        /// </summary>
        public TrackingState TrackingState
        {
            get
            {
                if (trackingObserver == null)
                {
                    return TrackingState.Unknown;
                }

                return trackingObserver.TrackingState;
            }
        }

        /// <summary>
        /// True if all local and peer coordinates known to the device have been found in the shared experience space.
        /// </summary>
        public bool AllCoordinatesLocated
        {
            get
            {
                bool allFound = participants.Count > 0;
                foreach (var participantPair in participants)
                {
                    if (participantPair.Value.Coordinate == null ||
                        participantPair.Value.IsLocatingSpatialCoordinate ||
                        !participantPair.Value.PeerSpatialCoordinateIsLocated)
                    {
                        allFound = false;
                        break;
                    }
                }

                return allFound;
            }
        }

        /// <summary>
        /// True if the device is currently in a localization session with a peer.
        /// </summary>
        public bool LocalizationRunning
        {
            get
            {
                return remoteLocalizationSessions.Count > 0 || currentLocalizationSession != null;
            }
        }

        public IReadOnlyCollection<ISpatialLocalizer> Localizers
        {
            get
            {
                return localizers.Values;
            }
        }

        public void RegisterSpatialLocalizer(ISpatialLocalizer localizer)
        {
            if (localizers.ContainsKey(localizer.SpatialLocalizerId))
            {
                Debug.LogError($"Cannot register multiple SpatialLocalizers with the same ID {localizer.SpatialLocalizerId}");
                return;
            }

            DebugLog($"Registering spatial localizer: {localizer.SpatialLocalizerId}");
            localizers.Add(localizer.SpatialLocalizerId, localizer);
        }

        public void UnregisterSpatialLocalizer(ISpatialLocalizer localizer)
        {
            if (!localizers.Remove(localizer.SpatialLocalizerId))
            {
                Debug.LogError($"Attempted to unregister SpatialLocalizer with ID {localizer.SpatialLocalizerId} that was not registered.");
            }
        }

        public void RegisterNetworkManager(INetworkManager networkManager)
        {
            if (!networkManagers.Add(networkManager))
            {
                Debug.LogError($"Attempted to register the same network manager multiple times");
                return;
            }

            RegisterEvents(networkManager);
        }

        public void UnregisterNetworkManager(INetworkManager networkManager)
        {
            if (!networkManagers.Remove(networkManager))
            {
                Debug.LogError($"Attempted to unregister a network manager that was not registered");
                return;
            }

            UnregisterEvents(networkManager);
        }

        /// <summary>
        /// Call to register an ITrackingObserver to use for determining tracking state.
        /// </summary>
        /// <param name="trackingObserver">Tracking observer used to determine tracking state.</param>
        public void RegisterTrackingObserver(ITrackingObserver trackingObserver)
        {
            if (this.trackingObserver != null)
            {
                Debug.LogError("Multiple tracking observers registered for the application.");
            }

            this.trackingObserver = trackingObserver;
        }

        /// <summary>
        /// Call to unregister an ITrackingObserver to use for determining tracking state.
        /// </summary>
        /// <param name="trackingObserver">Tracking observer used to determine tracking state.</param>
        public void UnregisterTrackingObserver(ITrackingObserver trackingObserver)
        {
            if (this.trackingObserver != trackingObserver)
            {
                Debug.LogWarning("Attempted to unregister tracking observer that wasn't registered.");
            }
            else
            {
                this.trackingObserver = null;
            }
        }

        public Task<bool> RunRemoteLocalizationAsync(SocketEndpoint socketEndpoint, Guid spatialLocalizerID, ISpatialLocalizationSettings settings)
        {
            DebugLog($"Initiating remote localization: {socketEndpoint.Address}, {spatialLocalizerID.ToString()}");
            if (remoteLocalizationSessions.TryGetValue(socketEndpoint, out var currentCompletionSource))
            {
                DebugLog("Canceling existing remote localization session: {socketEndpoint.Address}");
                currentCompletionSource.TrySetCanceled();
                remoteLocalizationSessions.Remove(socketEndpoint);
            }

            TaskCompletionSource<bool> taskCompletionSource = new TaskCompletionSource<bool>();
            remoteLocalizationSessions.Add(socketEndpoint, taskCompletionSource);

            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter message = new BinaryWriter(stream))
            {
                message.Write(LocalizeCommand);
                message.Write(spatialLocalizerID);
                settings.Serialize(message);

                socketEndpoint.Send(stream.ToArray());
            }

            return taskCompletionSource.Task;
        }

        public Task<bool> LocalizeAsync(SocketEndpoint socketEndpoint, Guid spatialLocalizerID, ISpatialLocalizationSettings settings)
        {
            DebugLog("LocalizeAsync");
            if (!participants.TryGetValue(socketEndpoint, out SpatialCoordinateSystemParticipant participant))
            {
                Debug.LogError($"Could not find a SpatialCoordinateSystemParticipant for SocketEndpoint {socketEndpoint.Address}");
                return Task.FromResult(false);
            }

            if (!localizers.TryGetValue(spatialLocalizerID, out ISpatialLocalizer localizer))
            {
                Debug.LogError($"Could not find a ISpatialLocalizer for spatialLocalizerID {spatialLocalizerID}");
                return Task.FromResult(false);
            }

            DebugLog("Returning a localization session.");
            return RunLocalizationSessionAsync(localizer, settings, participant);
        }

        protected override void OnDestroy()
        {
            foreach (INetworkManager networkManager in networkManagers)
            {
                UnregisterEvents(networkManager);
            }

            CleanUpParticipants();
        }

        private void Update()
        {
            foreach (var participant in participants.Values)
            {
                participant.EnsureStateChangesAreBroadcast();
            }
        }

        private void SendLocalizationCompleteCommand(SocketEndpoint socketEndpoint, bool localizationSuccessful)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter message = new BinaryWriter(stream))
            {
                message.Write(LocalizationCompleteCommand);
                message.Write(localizationSuccessful);

                socketEndpoint.Send(stream.ToArray());
            }
        }

        private void OnConnected(SocketEndpoint endpoint)
        {
            if (participants.TryGetValue(endpoint, out var existingParticipant))
            {
                Debug.LogWarning("SpatialCoordinateSystemParticipant connected that already existed.");
                return;
            }

            DebugLog($"Creating new SpatialCoordinateSystemParticipant, IPAddress: {endpoint.Address}, DebugLogging: {debugLogging}");

            SpatialCoordinateSystemParticipant participant = new SpatialCoordinateSystemParticipant(endpoint, debugVisual, debugVisualScale);
            participants[endpoint] = participant;
            participant.ShowDebugVisuals = showDebugVisuals;
            participant.SendSupportedLocalizersMessage(endpoint, localizers.Keys);

            if (ParticipantConnected == null)
            {
                DebugLog($"No ParticipantConnected event handlers exist");
            }
            else
            {
                DebugLog($"Invoking ParticipantConnected event");
                ParticipantConnected.Invoke(participant);
            }
        }

        private void OnDisconnected(SocketEndpoint endpoint)
        {
            if (participants.TryGetValue(endpoint, out var participant))
            {
                TryCleanupExistingLocalizationSession(participant);
                participant.Dispose();
                participants.Remove(endpoint);

                ParticipantDisconnected?.Invoke(participant);
            }

            if (remoteLocalizationSessions.TryGetValue(endpoint, out var completionSource))
            {
                completionSource.TrySetResult(false);
                remoteLocalizationSessions.Remove(endpoint);
            }
        }

        private void OnCoordinateStateReceived(SocketEndpoint socketEndpoint, string command, BinaryReader reader, int remainingDataSize)
        {
            if (!participants.TryGetValue(socketEndpoint, out SpatialCoordinateSystemParticipant participant))
            {
                Debug.LogWarning($"Failed to find a SpatialCoordinateSystemParticipant for an attached SocketEndpoint");
                return;
            }

            participant.ReadCoordinateStateMessage(reader);
        }

        private void OnSupportedLocalizersMessageReceived(SocketEndpoint socketEndpoint, string command, BinaryReader reader, int remainingDataSize)
        {
            if (!participants.TryGetValue(socketEndpoint, out SpatialCoordinateSystemParticipant participant))
            {
                Debug.LogWarning($"Failed to find a SpatialCoordinateSystemParticipant for an attached SocketEndpoint");
                return;
            }

            participant.ReadSupportedLocalizersMessage(reader);
        }

        private void OnLocalizationCompleteMessageReceived(SocketEndpoint socketEndpoint, string command, BinaryReader reader, int remainingDataSize)
        {
            bool localizationSuccessful = reader.ReadBoolean();

            if (!remoteLocalizationSessions.TryGetValue(socketEndpoint, out TaskCompletionSource<bool> taskSource))
            {
                DebugLog($"Remote session from endpoint {socketEndpoint.Address} completed but we were no longer tracking that session");
                return;
            }

            DebugLog($"Localization completed message received: {socketEndpoint.Address}");
            remoteLocalizationSessions.Remove(socketEndpoint);
            taskSource.TrySetResult(localizationSuccessful);
        }

        private async void OnLocalizeMessageReceived(SocketEndpoint socketEndpoint, string command, BinaryReader reader, int remainingDataSize)
        {
            DebugLog("LocalizeMessageReceived");
            if (!participants.TryGetValue(socketEndpoint, out SpatialCoordinateSystemParticipant participant))
            {
                Debug.LogError($"Could not find a SpatialCoordinateSystemParticipant for SocketEndpoint {socketEndpoint.Address}");
                SendLocalizationCompleteCommand(socketEndpoint, localizationSuccessful: false);
                return;
            }

            Guid spatialLocalizerID = reader.ReadGuid();

            if (!localizers.TryGetValue(spatialLocalizerID, out ISpatialLocalizer localizer))
            {
                Debug.LogError($"Request to begin localization with localizer {spatialLocalizerID} but no localizer with that ID was registered");
                SendLocalizationCompleteCommand(socketEndpoint, localizationSuccessful: false);
                return;
            }

            if (!localizer.TryDeserializeSettings(reader, out ISpatialLocalizationSettings settings))
            {
                Debug.LogError($"Failed to deserialize settings for localizer {spatialLocalizerID}");
                SendLocalizationCompleteCommand(socketEndpoint, localizationSuccessful: false);
                return;
            }

            bool localizationSuccessful = await RunLocalizationSessionAsync(localizer, settings, participant);

            // Ensure that the participant's fully-localized state is sent before sending the LocalizationComplete command (versus waiting
            // for the next Update). This way the remote peer receives the located state of the participant before they receive the notification
            // that this localization session completed.
            participant.EnsureStateChangesAreBroadcast();

            SendLocalizationCompleteCommand(socketEndpoint, localizationSuccessful);
        }

        private void OnParticipantDataReceived(SocketEndpoint endpoint, string command, BinaryReader reader, int remainingDataSize)
        {
            if (!TryGetSpatialCoordinateSystemParticipant(endpoint, out SpatialCoordinateSystemParticipant participant))
            {
                Debug.LogError($"Received participant localization data for a missing participant: {endpoint.Address}");
                return;
            }

            if (participant.CurrentLocalizationSession == null)
            {
                Debug.LogError($"Received participant localization data for a participant that is not currently running a localization session: {endpoint.Address}");
                return;
            }

            DebugLog($"Data received for participant: {endpoint.Address}, {command}");
            participant.CurrentLocalizationSession.OnDataReceived(reader);
        }

        private async Task<bool> RunLocalizationSessionAsync(ISpatialLocalizer localizer, ISpatialLocalizationSettings settings, SpatialCoordinateSystemParticipant participant)
        {
            if (!TryCleanupExistingLocalizationSession(participant))
            {
                DebugLog("Existing localization session with different participant prevented creating new localization session");
                return false;
            }

            if (!localizer.TryCreateLocalizationSession(participant, settings, out var localizationSession))
            {
                Debug.LogError($"Failed to create an ISpatialLocalizationSession from localizer {localizer.SpatialLocalizerId}");
                return false;
            }

            Task<bool> resultTask;
            bool startSession = false;
            var localizationSessionDetails = new LocalizationSessionDetails(localizationSession, participant);
            lock (localizationLock)
            {
                if (currentLocalizationSession != null)
                {
                    DebugLog($"Current localization session repopulated after cleanup, localization not performed.");
                    localizationSessionDetails.Session.Dispose();
                    resultTask = Task.FromResult(false);
                }
                else
                {
                    currentLocalizationSession = localizationSessionDetails;
                    localizationSessionDetails.Participant.CurrentLocalizationSession = localizationSessionDetails.Session;
                    resultTask = localizationSessionDetails.CompletionSource.Task;
                    startSession = true;
                }
            }

            if (startSession)
            {
                await Dispatcher.ScheduleAsync(async () =>
                {
                    try
                    {
                        // Some SpatialLocalizers/SpatialCoordinateServices key off of token cancellation for their logic flow.
                        // Therefore, we need to create a cancellation token even it is never actually cancelled by the SpatialCoordinateSystemManager.
                        using (var localizeCTS = new CancellationTokenSource())
                        {
                            var coordinate = await localizationSessionDetails.Session.LocalizeAsync(localizeCTS.Token);
                            bool succeeded = (coordinate != null);
                            localizationSessionDetails.Session.Dispose();
                            localizationSessionDetails.CompletionSource.TrySetResult(succeeded);

                            if (localizationSessionDetails.Participant.CurrentLocalizationSession == localizationSessionDetails.Session)
                            {
                                localizationSessionDetails.Participant.Coordinate = coordinate;
                                localizationSessionDetails.Participant.CurrentLocalizationSession = null;
                            }
                            else
                            {
                                Debug.LogWarning("Localization session completed but was no longer assigned to the associated participant");
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        DebugLog("Localization operation cancelled.");
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"Exception thrown localizing experience: {e.ToString()}");
                    }
                }, CancellationToken.None, true);
            }

            return await resultTask;
        }

        private bool TryCleanupExistingLocalizationSession(SpatialCoordinateSystemParticipant participant)
        {
            bool succeeded = true;
            LocalizationSessionDetails sessionToCancel = null;
            lock (localizationLock)
            {
                if (currentLocalizationSession != null)
                {
                    if (currentLocalizationSession.Participant == participant)
                    {
                        sessionToCancel = currentLocalizationSession;
                        currentLocalizationSession = null;
                    }
                    else
                    {
                        succeeded = false;
                    }
                }
            }

            if (sessionToCancel != null)
            {
                DebugLog($"Cancelling spatial localization session for participant: {participant?.SocketEndpoint?.Address ?? "Unknown"}");
                sessionToCancel.Session.Cancel();
                sessionToCancel.Session.Dispose();
                sessionToCancel.CompletionSource.TrySetResult(false);
            }

            return succeeded;
        }

        private void RegisterEvents(INetworkManager networkManager)
        {
            networkManager.Connected += OnConnected;
            networkManager.Disconnected += OnDisconnected;
            networkManager.RegisterCommandHandler(LocalizeCommand, OnLocalizeMessageReceived);
            networkManager.RegisterCommandHandler(LocalizationCompleteCommand, OnLocalizationCompleteMessageReceived);
            networkManager.RegisterCommandHandler(CoordinateStateMessageHeader, OnCoordinateStateReceived);
            networkManager.RegisterCommandHandler(SupportedLocalizersMessageHeader, OnSupportedLocalizersMessageReceived);
            networkManager.RegisterCommandHandler(SpatialCoordinateSystemParticipant.LocalizationDataExchangeCommand, OnParticipantDataReceived);
        }

        private void UnregisterEvents(INetworkManager networkManager)
        {
            networkManager.Connected -= OnConnected;
            networkManager.Disconnected -= OnDisconnected;
            networkManager.UnregisterCommandHandler(LocalizeCommand, OnLocalizeMessageReceived);
            networkManager.UnregisterCommandHandler(LocalizationCompleteCommand, OnLocalizationCompleteMessageReceived);
            networkManager.UnregisterCommandHandler(CoordinateStateMessageHeader, OnCoordinateStateReceived);
            networkManager.UnregisterCommandHandler(SupportedLocalizersMessageHeader, OnSupportedLocalizersMessageReceived);
            networkManager.UnregisterCommandHandler(SpatialCoordinateSystemParticipant.LocalizationDataExchangeCommand, OnParticipantDataReceived);
        }

        private void CleanUpParticipants()
        {
            foreach(var participant in participants)
            {
                if (participant.Value != null)
                {
                    participant.Value.Dispose();
                }
            }

            participants.Clear();
        }

        private void DebugLog(string message)
        {
            if (debugLogging)
            {
                Debug.Log($"SpatialCoordinateSystemManager: {message}");
            }
        }

        public bool TryGetSpatialCoordinateSystemParticipant(SocketEndpoint connectedEndpoint, out SpatialCoordinateSystemParticipant participant)
        {
            return participants.TryGetValue(connectedEndpoint, out participant);
        }
    }
}
