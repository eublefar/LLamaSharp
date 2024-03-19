﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LLama.Abstractions;
using LLama.Native;

namespace LLama.Batched;

struct AsyncMutex : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public AsyncMutex()
    {
    }

    public void Wait()
    {
        _semaphore.Wait();
    }

    public void Release()
    {
        _semaphore.Release(1);
    }

    public async Task WithMutexAsync(Func<Task> t)
    {
        await _semaphore.WaitAsync();

        try
        {
            await t();
        }
        finally
        {
            _semaphore.Release(1);
        }
    }

    public void Dispose()
    {
        _semaphore.Dispose();
    }
}

/// <summary>
/// A batched executor that can infer multiple separate "conversations" simultaneously.
/// </summary>
public sealed class BatchedExecutor
    : IDisposable
{
    private struct LogitCacheKey
    {
        public ulong Epoch;
        public int BatchIndex;
        public LLamaSeqId ConversationId;

        public override readonly bool Equals(object? obj)
        {
            return obj is LogitCacheKey key &&
                   Epoch == key.Epoch &&
                   BatchIndex == key.BatchIndex &&
                   ConversationId == key.ConversationId;
        }

        public override readonly int GetHashCode()
        {
            return (Epoch, BatchIndex, ConversationId).GetHashCode();
        }
    }

    private int _nextSequenceId;

    private readonly AsyncMutex _mutex = new AsyncMutex();

    private ConcurrentQueue<LLamaBatch> _batchQueue;
    private readonly ConcurrentDictionary<LogitCacheKey, float[]> _logitCache;

    /// <summary>
    /// Epoch is incremented every time Infer is called. Conversations can use this to keep track of
    /// whether they're waiting for inference, or can be sampled.
    /// </summary>
    internal ulong Epoch { get; private set; }

    /// <summary>
    /// The <see cref="LLamaContext"/> this executor is using
    /// </summary>
    public LLamaContext Context { get; }

    /// <summary>
    /// The <see cref="LLamaWeights"/> this executor is using
    /// </summary>
    public LLamaWeights Model { get; }

    /// <summary>
    /// Get the number of tokens in the batch, waiting for <see cref="Infer"/> to be called
    /// </summary>
    public int BatchedTokenCount
    {
        get
        {
            var count = 0;
            foreach (var batch in _batchQueue)
                count += batch.TokenCount;
            return count;
        }
    }

    /// <summary>
    /// Check if this executor has been disposed.
    /// </summary>
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// Create a new batched executor
    /// </summary>
    /// <param name="model">The model to use</param>
    /// <param name="contextParams">Parameters to create a new context</param>
    public BatchedExecutor(LLamaWeights model, IContextParams contextParams)
    {
        Model = model;
        _batchQueue = new ConcurrentQueue<LLamaBatch>();
        Context = model.CreateContext(contextParams);
        Epoch = 1;
        _logitCache = new ConcurrentDictionary<LogitCacheKey, float[]>();
    }

    /// <summary>
    /// Finalizer for BatchedExecutor
    /// </summary>
    ~BatchedExecutor()
    {
        Dispose();
    }

    /// <summary>
    /// Start a new <see cref="Conversation"/> with the given prompt
    /// </summary>
    /// <param name="prompt"></param>
    /// <returns></returns>
    public Conversation Prompt(string prompt)
    {
        if (IsDisposed)
            throw new ObjectDisposedException(nameof(BatchedExecutor));
        var conversation = new Conversation(this, GetNextSequenceId(), 0);
        conversation.Prompt(prompt);
        return conversation;
    }

    /// <summary>
    /// Run inference for all conversations in the batch which have pending tokens.
    ///
    /// If the result is `NoKvSlot` then there is not enough memory for inference, try disposing some conversation
    /// threads and running inference again.
    /// </summary>
    /// <param name="processAll">If true processes the whole Queue, otherwise only single batch</param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    public async Task<DecodeResult> Infer(bool processAll = true, CancellationToken cancellation = default)
    {
        if (IsDisposed)
            throw new ObjectDisposedException(nameof(BatchedExecutor));
        DecodeResult status = DecodeResult.Ok;
        await _mutex.WithMutexAsync(async () =>
        {
            if (processAll)
            {
                while (_batchQueue.TryPeek(out var batch))
                {
                    status = await Context.DecodeAsync(batch, cancellation);
                    if (status != DecodeResult.Ok)
                        break;
                    if (status == DecodeResult.Ok)
                    {
                        Epoch++;
                        _batchQueue.TryDequeue(out _);
                        AddToLogitCache(batch);
                        batch.Clear();
                    }
                }
                
            }
            else
            {
                if (_batchQueue.TryPeek(out var batch))
                {
                    var status = await Context.DecodeAsync(batch, cancellation);
                    if (status == DecodeResult.Ok)
                    {
                        Epoch++;
                        _batchQueue.TryDequeue(out _);
                        AddToLogitCache(batch);
                        batch.Clear();
                    }
                }
            }
        });
        return status;
    }

    internal float[] SampleLogits(ulong epoch, int batchIndex, LLamaSeqId conversationId)
    {
        var cacheKey = new LogitCacheKey
        {
            Epoch = epoch,
            BatchIndex = batchIndex,
            ConversationId = conversationId
        };
        if (_logitCache.TryRemove(cacheKey, out var logits))
        {
            return logits;
        }
        else
        {
            Console.WriteLine($"Epoch: {epoch}, BatchIndex: {batchIndex}, ConversationId: {conversationId}");
            Console.WriteLine($"Cache: {string.Join(", ", _logitCache.Keys.Select(k => $"Epoch: {k.Epoch}, BatchIndex: {k.BatchIndex}, ConversationId: {k.ConversationId}"))}");
        }
        throw new InvalidOperationException("Logits not found in cache");
    }

    /// <summary>
    /// Add a token to the batch for the given conversation.
    /// </summary>
    /// <param name="token"></param>
    /// <param name="endIndex"></param>
    /// <param name="ConversationId"></param>
    /// <param name="logits"></param>
    /// <returns></returns>
    internal (int batchIndex, ulong requiredEpochs) AddTokenToConversation(
        LLamaToken token, LLamaPos endIndex, LLamaSeqId ConversationId, bool logits)
    {
        _mutex.Wait();
        if (!_batchQueue.TryPeek(out var batch) || batch.TokenCount >= Context.Params.BatchSize)
        {
            batch = new LLamaBatch();
            _batchQueue.Enqueue(batch);
        }
        var batchIndex = batch.Add(token, endIndex, ConversationId, logits);
        var requiredEpochs = (ulong) _batchQueue.Count;
        _mutex.Release();
        return (batchIndex, requiredEpochs);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;
        foreach (var batch in _batchQueue)
            batch.Clear();
        _batchQueue = null;
        _mutex.Dispose();
        GC.SuppressFinalize(this);

        Context.Dispose();
    }

    internal LLamaSeqId GetNextSequenceId()
    {
        var id = checked((LLamaSeqId)_nextSequenceId);
        Interlocked.Increment(ref _nextSequenceId);
        return id;
    }

    internal void CopyConversationCache(LLamaSeqId from, LLamaSeqId dest, LLamaPos end)
    {
        // Assign tokens to the new sequence
        NativeApi.llama_kv_cache_seq_cp(Context.NativeHandle, from, dest, 0, end);

        // Copy logits to the new sequence
        foreach (var entry in _logitCache)
        {
            if (entry.Key.ConversationId == from)
            {
                var newKey = new LogitCacheKey
                {
                    Epoch = entry.Key.Epoch,
                    BatchIndex = entry.Key.BatchIndex,
                    ConversationId = dest
                };
                _logitCache.TryAdd(newKey, entry.Value);
            }
        }
    }

    internal void RemoveFromCache(LLamaSeqId conversationId, LLamaPos end)
    {
        foreach (var key in _logitCache.Keys)
        {
            if (key.ConversationId == conversationId)
            {
                _logitCache.TryRemove(key, out _);
            }
        }
        Context.NativeHandle.KvCacheRemove(conversationId, 0, end);
    }

    private void AddToLogitCache(LLamaBatch batch)
    {
        for(var i = 0; i < batch.Logits.Length; i++)
        {
            if (batch.Logits[i])
            {
                foreach (var seqId in batch.SequenceIds[i])
                {
                    var cacheKey = new LogitCacheKey
                    {
                        Epoch = Epoch,
                        BatchIndex = i,
                        ConversationId = seqId
                    };
                    _logitCache.TryAdd(
                        cacheKey,
                        Context.NativeHandle.GetLogitsIth(i).ToArray()
                    );
                }
            }
        }
    }
}