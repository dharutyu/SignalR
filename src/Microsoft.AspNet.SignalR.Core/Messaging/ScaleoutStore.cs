﻿using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Microsoft.AspNet.SignalR.Infrastructure;

namespace Microsoft.AspNet.SignalR.Messaging
{
    // Represents a message store that is backed by a ring buffer.
    [SuppressMessage("Microsoft.Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable", Justification = "The rate sampler doesn't need to be disposed")]
    public sealed class ScaleoutStore
    {
        private const uint _minFragmentCount = 4;

        [SuppressMessage("Microsoft.Performance", "CA1802:UseLiteralsWhereAppropriate", Justification = "It's conditional based on architecture")]
        private static readonly uint _maxFragmentSize = (IntPtr.Size == 4) ? (uint)16384 : (uint)8192; // guarantees that fragments never end up in the LOH

        private static readonly ArraySegment<ScaleoutMapping> _emptyArraySegment = new ArraySegment<ScaleoutMapping>(new ScaleoutMapping[0]);

        private Fragment[] _fragments;
        private readonly uint _fragmentSize;

        private long _minMessageId;
        private long _nextFreeMessageId;

        private ulong _minMappingId;
        private ScaleoutMapping _maxMapping;

        private readonly TraceSource _trace;
        private readonly string _tracePrefix;

        public ScaleoutStore(uint capacity)
            : this(capacity, trace: null, tracePrefix: null)
        {

        }

        // Creates a message store with the specified capacity. The actual capacity will be *at least* the
        // specified value. That is, GetMessages may return more data than 'capacity'.
        public ScaleoutStore(uint capacity, TraceSource trace, string tracePrefix)
        {
            // set a minimum capacity
            if (capacity < 32)
            {
                capacity = 32;
            }

            _trace = trace;
            _tracePrefix = tracePrefix;

            // Dynamically choose an appropriate number of fragments and the size of each fragment.
            // This is chosen to avoid allocations on the large object heap and to minimize contention
            // in the store. We allocate a small amount of additional space to act as an overflow
            // buffer; this increases throughput of the data structure.
            checked
            {
                uint fragmentCount = Math.Max(_minFragmentCount, capacity / _maxFragmentSize);
                _fragmentSize = Math.Min((capacity + fragmentCount - 1) / fragmentCount, _maxFragmentSize);
                _fragments = new Fragment[fragmentCount + 1]; // +1 for the overflow buffer
            }
        }

        internal ulong MinMappingId
        {
            get
            {
                return _minMappingId;
            }
        }

        public ScaleoutMapping MaxMapping
        {
            get
            {
                return _maxMapping;
            }
        }

        public uint FragmentSize
        {
            get
            {
                return _fragmentSize;
            }
        }

        public int FragmentCount
        {
            get
            {
                return _fragments.Length;
            }
        }

        // Adds a message to the store. Returns the ID of the newly added message.
        public ulong Add(ScaleoutMapping mapping)
        {
            // keep looping in TryAddImpl until it succeeds
            ulong newMessageId;
            while (!TryAddImpl(mapping, out newMessageId)) ;

            // When TryAddImpl succeeds, record the fact that a message was just added to the
            // store. We increment the next free id rather than set it explicitly since
            // multiple threads might be trying to write simultaneously. There is a nifty
            // side effect to this: _nextFreeMessageId will *always* return the total number
            // of messages that *all* threads agree have ever been added to the store. (The
            // actual number may be higher, but this field will eventually catch up as threads
            // flush data.)
            Interlocked.Increment(ref _nextFreeMessageId);
            return newMessageId;
        }

        private void GetFragmentOffsets(ulong messageId, out ulong fragmentNum, out int idxIntoFragmentsArray, out int idxIntoFragment)
        {
            fragmentNum = messageId / _fragmentSize;

            // from the bucket number, we can figure out where in _fragments this data sits
            idxIntoFragmentsArray = (int)(fragmentNum % (uint)_fragments.Length);
            idxIntoFragment = (int)(messageId % _fragmentSize);
        }

        private int GetFragmentOffset(ulong messageId)
        {
            ulong fragmentNum = messageId / _fragmentSize;

            return (int)(fragmentNum % (uint)_fragments.Length);
        }

        private ulong GetMessageId(ulong fragmentNum, uint offset)
        {
            return fragmentNum * _fragmentSize + offset;
        }

        private bool TryAddImpl(ScaleoutMapping mapping, out ulong newMessageId)
        {
            ulong nextFreeMessageId = (ulong)Volatile.Read(ref _nextFreeMessageId);

            // locate the fragment containing the next free id, which is where we should write
            ulong fragmentNum;
            int idxIntoFragmentsArray, idxIntoFragment;
            GetFragmentOffsets(nextFreeMessageId, out fragmentNum, out idxIntoFragmentsArray, out idxIntoFragment);
            Fragment fragment = _fragments[idxIntoFragmentsArray];

            if (fragment == null || fragment.FragmentNum < fragmentNum)
            {
                // the fragment is outdated (or non-existent) and must be replaced
                bool overwrite = fragment != null && fragment.FragmentNum < fragmentNum;

                if (idxIntoFragment == 0)
                {
                    // this thread is responsible for creating the fragment
                    Fragment newFragment = new Fragment(fragmentNum, _fragmentSize);
                    newFragment.Data[0] = mapping;
                    Fragment existingFragment = Interlocked.CompareExchange(ref _fragments[idxIntoFragmentsArray], newFragment, fragment);
                    if (existingFragment == fragment)
                    {
                        newMessageId = GetMessageId(fragmentNum, offset: 0);
                        newFragment.MinId = newMessageId;
                        newFragment.Length = 1;
                        newFragment.MaxId = GetMessageId(fragmentNum, offset: _fragmentSize - 1);
                        _maxMapping = mapping;

                        // Move the minimum id when we overwrite
                        if (overwrite)
                        {
                            _minMessageId = (long)(existingFragment.MaxId + 1);
                            _minMappingId = existingFragment.MaxId;
                        }
                        else if (idxIntoFragmentsArray == 0)
                        {
                            _minMappingId = mapping.Id;
                        }

                        return true;
                    }
                }

                // another thread is responsible for updating the fragment, so fall to bottom of method
            }
            else if (fragment.FragmentNum == fragmentNum)
            {
                // the fragment is valid, and we can just try writing into it until we reach the end of the fragment
                ScaleoutMapping[] fragmentData = fragment.Data;
                for (int i = idxIntoFragment; i < fragmentData.Length; i++)
                {
                    ScaleoutMapping originalMapping = Interlocked.CompareExchange(ref fragmentData[i], mapping, null);
                    if (originalMapping == null)
                    {
                        newMessageId = GetMessageId(fragmentNum, offset: (uint)i);
                        fragment.Length++;
                        _maxMapping = fragmentData[i];
                        return true;
                    }
                }

                // another thread used the last open space in this fragment, so fall to bottom of method
            }

            // failure; caller will retry operation
            newMessageId = 0;
            return false;
        }

        public MessageStoreResult<ScaleoutMapping> GetMessages(ulong firstMessageIdRequestedByClient, string connectionId, bool log)
        {
            ulong nextFreeMessageId = (ulong)Volatile.Read(ref _nextFreeMessageId);

            // Case 1:
            // The client is already up-to-date with the message store, so we return no data.
            if (nextFreeMessageId <= firstMessageIdRequestedByClient)
            {
                return new MessageStoreResult<ScaleoutMapping>(firstMessageIdRequestedByClient, _emptyArraySegment, hasMoreData: false);
            }

            // look for the fragment containing the start of the data requested by the client
            ulong fragmentNum;
            int idxIntoFragmentsArray, idxIntoFragment;
            GetFragmentOffsets(firstMessageIdRequestedByClient, out fragmentNum, out idxIntoFragmentsArray, out idxIntoFragment);
            Fragment thisFragment = _fragments[idxIntoFragmentsArray];
            ulong firstMessageIdInThisFragment = GetMessageId(thisFragment.FragmentNum, offset: 0);
            ulong firstMessageIdInNextFragment = firstMessageIdInThisFragment + _fragmentSize;

            // Case 2:
            // This fragment contains the first part of the data the client requested.
            if (firstMessageIdInThisFragment <= firstMessageIdRequestedByClient && firstMessageIdRequestedByClient < firstMessageIdInNextFragment)
            {
                int count = (int)(Math.Min(nextFreeMessageId, firstMessageIdInNextFragment) - firstMessageIdRequestedByClient);

                var retMessages = new ArraySegment<ScaleoutMapping>(thisFragment.Data, idxIntoFragment, count);

                return new MessageStoreResult<ScaleoutMapping>(firstMessageIdRequestedByClient, retMessages, hasMoreData: (nextFreeMessageId > firstMessageIdInNextFragment));
            }

            // Case 3:
            // The client has missed messages, so we need to send him the earliest fragment we have.

            if (log)
            {
                Trace("{0}: The client has missed messages, so we need to send him the earliest fragment we have.", connectionId);
            }

            while (true)
            {
                GetFragmentOffsets(nextFreeMessageId, out fragmentNum, out idxIntoFragmentsArray, out idxIntoFragment);
                Fragment tailFragment = _fragments[(idxIntoFragmentsArray + 1) % _fragments.Length];
                if (tailFragment.FragmentNum < fragmentNum)
                {
                    firstMessageIdInThisFragment = GetMessageId(tailFragment.FragmentNum, offset: 0);

                    return new MessageStoreResult<ScaleoutMapping>(firstMessageIdInThisFragment, new ArraySegment<ScaleoutMapping>(tailFragment.Data, 0, tailFragment.Length), hasMoreData: true);
                }
                nextFreeMessageId = (ulong)Volatile.Read(ref _nextFreeMessageId);
            }
        }

        public MessageStoreResult<ScaleoutMapping> GetMessagesByMappingId(ulong mappingId)
        {
            return GetMessagesByMappingId(mappingId, connectionId: null, log: false);
        }

        public MessageStoreResult<ScaleoutMapping> GetMessagesByMappingId(ulong mappingId, string connectionId, bool log)
        {
            if (log)
            {
                Trace("{0}: GetMessagesByMappingId({1}) {2} {3}", connectionId, mappingId, _minMappingId, _maxMapping == null ? 0 : _maxMapping.Id);
            }

            var minMessageId = (ulong)Volatile.Read(ref _minMessageId);
            bool expiredMappingId = false;

            int idxIntoFragment;
            // look for the fragment containing the start of the data requested by the client
            Fragment thisFragment;
            if (TryGetFragmentFromMappingId(mappingId, out thisFragment))
            {
                if (thisFragment.TrySearch(mappingId, out idxIntoFragment))
                {
                    // Skip the first message
                    idxIntoFragment++;
                    ulong firstMessageIdRequestedByClient = GetMessageId(thisFragment.FragmentNum, (uint)idxIntoFragment);

                    return GetMessages(firstMessageIdRequestedByClient, connectionId, log);
                }
                else
                {
                    if (log)
                    {
                        Trace("{0}: TrySearch({1}) failed {2} {3}", connectionId, mappingId, thisFragment.MinValue, thisFragment.MaxValue);
                    }

                    // We assume that if you fall between the range but we can't find a cursor
                    // then we've been reset
                    expiredMappingId = true;
                }
            }

            // If we're expired or we're at the first mapping or we're lower than the 
            // min then get everything
            if (expiredMappingId || mappingId <= _minMappingId)
            {
                if (log)
                {
                    Trace("{0}: expiredMappingId = {1} || Less than the min?: {2} <= {3} = {4}", connectionId, expiredMappingId, mappingId, _minMappingId, mappingId <= _minMappingId);

                    Trace("{0}: GetAllMessages()", connectionId);
                }

                return GetAllMessages(minMessageId);
            }

            //if (_maxMapping != null)
            //{
            //    int delta = mappingId - _maxMapping.Id;

            //    if (delta > 10000)
            //    {
            //        if (log)
            //        {
            //            Trace("{0}: GetAllMessages()", connectionId);
            //        }

            //        return GetAllMessages(minMessageId);
            //    }
            //}

            return new MessageStoreResult<ScaleoutMapping>(0, _emptyArraySegment, hasMoreData: false);
        }

        private MessageStoreResult<ScaleoutMapping> GetAllMessages(ulong minMessageId)
        {
            ulong fragmentNum;
            int idxIntoFragmentsArray, idxIntoFragment;
            GetFragmentOffsets(minMessageId, out fragmentNum, out idxIntoFragmentsArray, out idxIntoFragment);

            Fragment fragment = _fragments[idxIntoFragmentsArray];

            if (fragment == null)
            {
                return new MessageStoreResult<ScaleoutMapping>(minMessageId, _emptyArraySegment, hasMoreData: false);
            }

            var firstMessageIdInThisFragment = GetMessageId(fragment.FragmentNum, offset: 0);

            var messages = new ArraySegment<ScaleoutMapping>(fragment.Data, 0, fragment.Length);

            return new MessageStoreResult<ScaleoutMapping>(firstMessageIdInThisFragment, messages, hasMoreData: true);
        }

        public void Trace(string value, params object[] args)
        {
            if (_trace != null)
            {
                _trace.TraceInformation(_tracePrefix + ": " + value, args);
            }
        }

        internal bool TryGetFragmentFromMappingId(ulong mappingId, out Fragment fragment)
        {
            long low = _minMessageId;
            long high = _nextFreeMessageId;

            while (low <= high)
            {
                var mid = (ulong)((low + high) / 2);

                int midOffset = GetFragmentOffset(mid);

                fragment = _fragments[midOffset];

                if (fragment == null)
                {
                    return false;
                }

                if (mappingId < fragment.MinValue)
                {
                    high = (long)(fragment.MinId - 1);
                }
                else if (mappingId > fragment.MaxValue)
                {
                    low = (long)(fragment.MaxId + 1);
                }
                else if (fragment.HasValue(mappingId))
                {
                    return true;
                }
            }

            fragment = null;
            return false;
        }

        internal sealed class Fragment
        {
            public readonly ulong FragmentNum;
            public readonly ScaleoutMapping[] Data;
            public int Length;
            public ulong MinId;
            public ulong MaxId;

            public Fragment(ulong fragmentNum, uint fragmentSize)
            {
                FragmentNum = fragmentNum;
                Data = new ScaleoutMapping[fragmentSize];
            }

            public ulong? MinValue
            {
                get
                {
                    var mapping = Data[0];
                    if (mapping != null)
                    {
                        return mapping.Id;
                    }

                    return null;
                }
            }

            public ulong? MaxValue
            {
                get
                {
                    ScaleoutMapping mapping = null;

                    if (Length == 0)
                    {
                        mapping = Data[Length];
                    }
                    else
                    {
                        mapping = Data[Length - 1];
                    }

                    if (mapping != null)
                    {
                        return mapping.Id;
                    }

                    return null;
                }
            }

            public bool HasValue(ulong id)
            {
                return id >= MinValue && id <= MaxValue;
            }

            public bool TrySearch(ulong id, out int index)
            {
                int low = 0;
                int high = Length;

                while (low <= high)
                {
                    int mid = (low + high) / 2;

                    ScaleoutMapping mapping = Data[mid];

                    if (id < mapping.Id)
                    {
                        high = mid - 1;
                    }
                    else if (id > mapping.Id)
                    {
                        low = mid + 1;
                    }
                    else if (id == mapping.Id)
                    {
                        index = mid;
                        return true;
                    }
                }

                index = -1;
                return false;
            }
        }
    }

}
