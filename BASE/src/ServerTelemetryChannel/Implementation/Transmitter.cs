﻿namespace Microsoft.ApplicationInsights.WindowsServer.TelemetryChannel.Implementation
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.ApplicationInsights.Channel;
    using Microsoft.ApplicationInsights.Channel.Implementation;

    /// <summary>
    /// Implements throttled and persisted transmission of telemetry to Application Insights. 
    /// </summary>
    internal class Transmitter : IDisposable
    {
        internal readonly TransmissionSender Sender;
        internal readonly TransmissionBuffer Buffer;        
        internal readonly TransmissionStorage Storage;        
        private readonly IEnumerable<TransmissionPolicy> policies;
        private readonly BackoffLogicManager backoffLogicManager;
        private readonly Task<bool> successTask = Task.FromResult(true);
        private readonly Task<bool> failedTask = Task.FromResult(false);

        private bool arePoliciesApplied;
        private int maxSenderCapacity;
        private int maxBufferCapacity;
        private long maxStorageCapacity;

        /// <summary>
        /// Initializes a new instance of the <see cref="Transmitter" /> class. Used only for UTs.
        /// </summary>
        [SuppressMessage("Microsoft.Usage", "CA2214:DoNotCallOverridableMethodsInConstructors", Justification = "TODO: change this in future submits.")]
        internal Transmitter(
            TransmissionSender sender = null, 
            TransmissionBuffer transmissionBuffer = null, 
            TransmissionStorage storage = null, 
            IEnumerable<TransmissionPolicy> policies = null,
            BackoffLogicManager backoffLogicManager = null)
        { 
            this.backoffLogicManager = backoffLogicManager ?? new BackoffLogicManager();
            this.Sender = sender ?? new TransmissionSender();
            this.Sender.TransmissionSent += this.HandleSenderTransmissionSentEvent;
            this.maxSenderCapacity = this.Sender.Capacity;

            this.Buffer = transmissionBuffer ?? new TransmissionBuffer();
            this.Buffer.TransmissionDequeued += this.HandleBufferTransmissionDequeuedEvent;
            this.maxBufferCapacity = this.Buffer.Capacity;

            this.Storage = storage ?? new TransmissionStorage();
            this.maxStorageCapacity = this.Storage.Capacity;

            this.policies = policies ?? Enumerable.Empty<TransmissionPolicy>();
            foreach (TransmissionPolicy policy in this.policies)
            {
                policy.Initialize(this);
            }
        }

        public event EventHandler<TransmissionProcessedEventArgs> TransmissionSent;

        public string StorageFolder { get; set; }        

        public int MaxBufferCapacity
        {
            get
            {
                return this.maxBufferCapacity;
            }

            set
            {
                this.maxBufferCapacity = value;
                this.ApplyPoliciesIfAlreadyApplied();
            }
        }

        public int MaxSenderCapacity 
        { 
            get
            {
                return this.maxSenderCapacity;
            }

            set
            {
                this.maxSenderCapacity = value;
                this.ApplyPoliciesIfAlreadyApplied();
            }
        }

        public long MaxStorageCapacity
        {
            get
            {
                return this.maxStorageCapacity;
            }

            set
            {
                this.maxStorageCapacity = value;
                this.ApplyPoliciesIfAlreadyApplied();
            }
        }

        public bool ApplyThrottle
        {
            get { return this.Sender.ApplyThrottle; }
            set { this.Sender.ApplyThrottle = value; }
        }

        public int ThrottleLimit
        {
            get { return this.Sender.ThrottleLimit; }
            set { this.Sender.ThrottleLimit = value; }
        }

        public int ThrottleWindow
        {
            get { return this.Sender.ThrottleWindow; }
            set { this.Sender.ThrottleWindow = value; }
        }

        public BackoffLogicManager BackoffLogicManager
        {
            get { return this.backoffLogicManager; }
        }

        /// <summary>
        /// Releases resources used by this <see cref="Transmitter"/> instance.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        internal virtual void Initialize()
        {
            this.Storage.Initialize(new ApplicationFolderProvider(this.StorageFolder));
        }

        internal virtual void Enqueue(Transmission transmission)
        {
            if (transmission == null)
            {
                return;
            }

            TelemetryChannelEventSource.Log.TransmitterEnqueue(transmission.Id);

            if (!this.arePoliciesApplied)
            {
                this.ApplyPolicies();
            }

            Func<Transmission> transmissionGetter = () => transmission;

            if (this.Sender.Enqueue(transmissionGetter))
            {
                return;
            }

            TelemetryChannelEventSource.Log.TransmitterSenderSkipped(transmission.Id);

            if (this.Buffer.Enqueue(transmissionGetter))
            {
                return;
            }

            TelemetryChannelEventSource.Log.TransmitterBufferSkipped(transmission.Id);
            TelemetryChannelEventSource.Log.LastBackendResponseWhenPutToStorage(this.BackoffLogicManager.LastStatusCode, this.BackoffLogicManager.CurrentDelay.TotalSeconds);

            if (!this.Storage.Enqueue(transmissionGetter))
            {
                transmission.HasFlushTask = false;
                TelemetryChannelEventSource.Log.TransmitterStorageSkipped(transmission.Id);
            }
        }

        internal Task<bool> FlushAsync(Transmission transmission, CancellationToken cancellationToken)
        {
            TaskStatus taskStatus = TaskStatus.Canceled;
            if (!cancellationToken.IsCancellationRequested)
            {
                transmission.HasFlushTask = true;
                this.Enqueue(transmission);

                try
                {
                    this.Storage.IncrementFlushAsyncCounter();
                    taskStatus = this.MoveTransmissionsAndWaitForSender(transmission.FlushAsyncId, cancellationToken);
                }
                catch (Exception exp)
                {
                    taskStatus = TaskStatus.Faulted;
                    TelemetryChannelEventSource.Log.TransmissionFlushAsyncWarning(exp.ToString());
                }
                finally
                {
                    this.Storage.DecrementFlushAsyncCounter();
                }
            }

            Task<bool> flushTaskStatus = null;
            if (taskStatus == TaskStatus.Canceled)
            {
                flushTaskStatus = TaskEx.FromCanceled<bool>(cancellationToken);
            }
            else if (taskStatus == TaskStatus.RanToCompletion && transmission.HasFlushTask)
            {
                flushTaskStatus = this.successTask;
            }
            else
            {
                flushTaskStatus = this.failedTask;
            }

            return flushTaskStatus;
        }

        internal Task<bool> MoveTransmissionsAndWaitForSender(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return TaskEx.FromCanceled<bool>(cancellationToken);
            }

            var isStorageEnqueueSuccess = MoveTransmissions(this.Buffer.Dequeue, this.Storage.Enqueue, this.Buffer.Size);  
            TelemetryChannelEventSource.Log.MovedFromBufferToStorage();
            var senderStatus = this.Sender.WaitForPreviousTransmissionsToComplete(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
            if (senderStatus == TaskStatus.Canceled)
            {
                return TaskEx.FromCanceled<bool>(cancellationToken);
            }

            return senderStatus == TaskStatus.RanToCompletion && isStorageEnqueueSuccess ? Task.FromResult(true) : Task.FromResult(false); 
        }

        internal TaskStatus MoveTransmissionsAndWaitForSender(long transmissionFlushAsyncId, CancellationToken cancellationToken)
        {
            var isStorageEnqueueSuccess = MoveTransmissions(this.Buffer.Dequeue, this.Storage.Enqueue, this.Buffer.Size);
            TelemetryChannelEventSource.Log.MovedFromBufferToStorage();
            var senderStatus = this.Sender.WaitForPreviousTransmissionsToComplete(transmissionFlushAsyncId, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();

            if (!isStorageEnqueueSuccess && senderStatus != TaskStatus.Canceled)
            {
                return TaskStatus.Faulted;
            }

            return senderStatus;
        }

        internal virtual void ApplyPolicies()
        {
            this.arePoliciesApplied = true;
            this.UpdateComponentCapacitiesFromPolicies();

            if (this.Sender.Capacity > 0 && this.Buffer.Size == 0)
            {
                // Start sending immediately, no need to wait for the buffer to fill up
                MoveTransmissions(this.Storage.Dequeue, this.Sender.Enqueue);
                TelemetryChannelEventSource.Log.MovedFromStorageToSender();
            }

            if (this.Buffer.Capacity > 0)
            {
                MoveTransmissions(this.Storage.Dequeue, this.Buffer.Enqueue);
                TelemetryChannelEventSource.Log.MovedFromStorageToBuffer();
            }
            else
            {
                MoveTransmissions(this.Buffer.Dequeue, this.Storage.Enqueue);
                TelemetryChannelEventSource.Log.MovedFromBufferToStorage();
                this.EmptyBuffer();
            }

            if (this.Storage.Capacity == 0)
            {
                this.EmptyStorage();
            }

            if (this.Sender.Capacity > 0)
            {
                MoveTransmissions(this.Buffer.Dequeue, this.Sender.Enqueue);
                TelemetryChannelEventSource.Log.MovedFromBufferToSender();
            }
        }

        internal void EmptyBuffer()
        {
            TelemetryChannelEventSource.Log.TransmitterEmptyBuffer();
            while (this.Buffer.Dequeue() != null)
            {
            }
        }

        internal void EmptyStorage()
        {
            TelemetryChannelEventSource.Log.TransmitterEmptyStorage();
            while (this.Storage.Dequeue() != null)
            {
            }
        }

        protected void OnTransmissionSent(TransmissionProcessedEventArgs e)
        {
            this.TransmissionSent?.Invoke(this, e);
        }

        private static void MoveTransmissions(Func<Transmission> dequeue, Func<Func<Transmission>, bool> enqueue)
        {
            bool transmissionMoved;
            do
            {
                transmissionMoved = enqueue(dequeue);
            }
            while (transmissionMoved);
        }

        private static bool MoveTransmissions(Func<Transmission> dequeue, Func<Func<Transmission>, bool> enqueue, long size)
        {          
            bool transmissionMoved = false;
            do
            {
                var transmission = dequeue();
                if (transmission == null)
                {
                    transmissionMoved = true;
                    break;
                }

                transmissionMoved = enqueue(() => transmission);
                size -= transmission.Content.Length;
            }
            while (transmissionMoved && size > 0);

            return transmissionMoved;
        }

        private void ApplyPoliciesIfAlreadyApplied()
        {
            if (this.arePoliciesApplied)
            {
                try
                {
                    this.ApplyPolicies();
                }
                catch (Exception exp)
                {
                    TelemetryChannelEventSource.Log.ExceptionHandlerStartExceptionWarning(exp.ToString());
                }
            }
        }

        private int? CalculateCapacity(Func<TransmissionPolicy, int?> getMaxPolicyCapacity)
        {
            int? maxComponentCapacity = null;
            foreach (TransmissionPolicy policy in this.policies)
            {
                int? maxPolicyCapacity = getMaxPolicyCapacity(policy);
                if (maxPolicyCapacity != null)
                {
                    maxComponentCapacity = maxComponentCapacity == null ? maxPolicyCapacity : Math.Min(maxComponentCapacity.Value, maxPolicyCapacity.Value);
                }
            }

            return maxComponentCapacity;
        }

        private void HandleSenderTransmissionSentEvent(object sender, TransmissionProcessedEventArgs e)
        {
            this.OnTransmissionSent(e);
            
            try
            {
                MoveTransmissions(this.Buffer.Dequeue, this.Sender.Enqueue);
            }
            catch (Exception exp)
            {
                TelemetryChannelEventSource.Log.ExceptionHandlerStartExceptionWarning(exp.ToString());
            }
        }

        private void HandleBufferTransmissionDequeuedEvent(object sender, TransmissionProcessedEventArgs e)
        {
            try
            {
                MoveTransmissions(this.Storage.Dequeue, this.Buffer.Enqueue);
            }
            catch (Exception exp)
            {
                TelemetryChannelEventSource.Log.ExceptionHandlerStartExceptionWarning(exp.ToString());
            }
        }

        private void UpdateComponentCapacitiesFromPolicies()
        {
            this.Sender.Capacity = this.CalculateCapacity(policy => policy.MaxSenderCapacity) ?? this.maxSenderCapacity;
            this.Buffer.Capacity = this.CalculateCapacity(policy => policy.MaxBufferCapacity) ?? this.maxBufferCapacity;
            this.Storage.Capacity = this.CalculateCapacity(policy => policy.MaxStorageCapacity) ?? this.maxStorageCapacity;
        }

        private void Dispose(bool disposing)
        {
            if (disposing && this.policies != null)
            {
                foreach (var policy in this.policies.OfType<IDisposable>())
                {
                    policy.Dispose();
                }

                if (this.Storage != null)
                {
                    this.Storage.Dispose();
                }
            }            
        }
    }
}
