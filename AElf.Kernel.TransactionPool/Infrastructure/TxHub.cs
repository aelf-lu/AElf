using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AElf.Common;
using AElf.Kernel.Blockchain.Application;
using AElf.Kernel.Blockchain.Domain;
using AElf.Kernel.Blockchain.Events;
using AElf.Kernel.TransactionPool.Application;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus.Local;

namespace AElf.Kernel.TransactionPool.Infrastructure
{
    public class TxHub : ITxHub, ISingletonDependency
    {
        public ILogger<TxHub> Logger { get; set; }

        private readonly ITransactionManager _transactionManager;
        private readonly IBlockchainService _blockchainService;

        private readonly Dictionary<Hash, TransactionReceipt> _allTransactions =
            new Dictionary<Hash, TransactionReceipt>();

        private Dictionary<Hash, TransactionReceipt> _validated = new Dictionary<Hash, TransactionReceipt>();

        private Dictionary<ulong, Dictionary<Hash, TransactionReceipt>> _invalidatedByBlock =
            new Dictionary<ulong, Dictionary<Hash, TransactionReceipt>>();

        private Dictionary<ulong, Dictionary<Hash, TransactionReceipt>> _expiredByExpiryBlock =
            new Dictionary<ulong, Dictionary<Hash, TransactionReceipt>>();

        private Dictionary<ulong, Dictionary<Hash, TransactionReceipt>> _futureByBlock =
            new Dictionary<ulong, Dictionary<Hash, TransactionReceipt>>();

        private ulong _bestChainHeight = ChainConsts.GenesisBlockHeight - 1;
        private Hash _bestChainHash = Hash.Genesis;

        public int ChainId { get; private set; }
        public ILocalEventBus LocalEventBus { get; set; }

        public TxHub(ITransactionManager transactionManager, IBlockchainService blockchainService)
        {
            Logger = NullLogger<TxHub>.Instance;
            _transactionManager = transactionManager;
            _blockchainService = blockchainService;
            LocalEventBus = NullLocalEventBus.Instance;
        }

        public async Task<ExecutableTransactionSet> GetExecutableTransactionSetAsync()
        {
            var chain = await _blockchainService.GetChainAsync(ChainId);
            if (chain.BestChainHash != _bestChainHash)
            {
                Logger.LogWarning(
                    $"Attempting to retrieve executable transactions while best chain records don't macth.");
                return new ExecutableTransactionSet()
                {
                    ChainId = ChainId,
                    PreviousBlockHash = _bestChainHash,
                    PreviousBlockHeight = _bestChainHeight
                };
            }

            var output = new ExecutableTransactionSet()
            {
                ChainId = ChainId,
                PreviousBlockHash = _bestChainHash,
                PreviousBlockHeight = _bestChainHeight
            };
            output.Transactions.AddRange(_validated.Values.Select(x => x.Transaction));

            return output;
        }

        public Task<TransactionReceipt> GetTransactionReceiptAsync(Hash transactionId)
        {
            _allTransactions.TryGetValue(transactionId, out var receipt);
            return Task.FromResult(receipt);
        }

        public void Dispose()
        {
        }

        public async Task<IDisposable> StartAsync(int chainId)
        {
            ChainId = chainId;
            return this;
        }

        public async Task StopAsync()
        {
        }


        #region Private Methods

        #region Private Static Methods

        private static void AddToCollection(Dictionary<ulong, Dictionary<Hash, TransactionReceipt>> collection,
            TransactionReceipt receipt)
        {
            if (!collection.TryGetValue(receipt.Transaction.RefBlockNumber, out var receipts))
            {
                receipts = new Dictionary<Hash, TransactionReceipt>();
                collection.Add(receipt.Transaction.RefBlockNumber, receipts);
            }

            receipts.Add(receipt.TransactionId, receipt);
        }

        private static void CheckPrefixForOne(TransactionReceipt receipt, ByteString prefix,
            ulong bestChainHeight)
        {
            if (receipt.Transaction.GetExpiryBlockNumber() <= bestChainHeight)
            {
                receipt.RefBlockStatus = RefBlockStatus.RefBlockExpired;
                return;
            }

            if (prefix == null)
            {
                receipt.RefBlockStatus = RefBlockStatus.FutureRefBlock;
                return;
            }

            if (receipt.Transaction.RefBlockPrefix == prefix)
            {
                receipt.RefBlockStatus = RefBlockStatus.RefBlockValid;
                return;
            }

            receipt.RefBlockStatus = RefBlockStatus.RefBlockInvalid;
        }

        #endregion

        private async Task<ByteString> GetPrefixByHeightAsync(Chain chain, ulong height, Hash bestChainHash)
        {
            var hash = await _blockchainService.GetBlockHashByHeightAsync(chain, height, bestChainHash);
            return hash == null ? null : ByteString.CopyFrom(hash.DumpByteArray().Take(4).ToArray());
        }

        private async Task<ByteString> GetPrefixByHeightAsync(int chainId, ulong height, Hash bestChainHash)
        {
            var chain = await _blockchainService.GetChainAsync(chainId);
            return await GetPrefixByHeightAsync(chain, height, bestChainHash);
        }

        private async Task<Dictionary<ulong, ByteString>> GetPrefixesByHeightAsync(int chainId,
            IEnumerable<ulong> heights, Hash bestChainHash)
        {
            var prefixes = new Dictionary<ulong, ByteString>();
            var chain = await _blockchainService.GetChainAsync(chainId);
            foreach (var h in heights)
            {
                var prefix = await GetPrefixByHeightAsync(chain, h, bestChainHash);
                prefixes.Add(h, prefix);
            }

            return prefixes;
        }

        private void ResetCurrentCollections()
        {
            _expiredByExpiryBlock = new Dictionary<ulong, Dictionary<Hash, TransactionReceipt>>();
            _invalidatedByBlock = new Dictionary<ulong, Dictionary<Hash, TransactionReceipt>>();
            _futureByBlock = new Dictionary<ulong, Dictionary<Hash, TransactionReceipt>>();
            _validated = new Dictionary<Hash, TransactionReceipt>();
        }

        private void AddToRespectiveCurrentCollection(TransactionReceipt receipt)
        {
            switch (receipt.RefBlockStatus)
            {
                case RefBlockStatus.RefBlockExpired:
                    AddToCollection(_expiredByExpiryBlock, receipt);
                    break;
                case RefBlockStatus.FutureRefBlock:
                    AddToCollection(_futureByBlock, receipt);
                    break;
                case RefBlockStatus.RefBlockInvalid:
                    AddToCollection(_invalidatedByBlock, receipt);
                    break;
                case RefBlockStatus.RefBlockValid:
                    _validated.Add(receipt.TransactionId, receipt);
                    break;
            }
        }

        #endregion

        #region Event Handler Methods

        public async Task HandleTransactionsReceivedAsync(TransactionsReceivedEvent eventData)
        {
            if (ChainId != eventData.ChainId)
            {
                return;
            }

            foreach (var transaction in eventData.Transactions)
            {
                var receipt = new TransactionReceipt(transaction);

                if (_allTransactions.ContainsKey(receipt.TransactionId))
                {
                    continue;
                }

                var txn = await _transactionManager.GetTransaction(receipt.TransactionId);
                if (txn != null)
                {
                    continue;
                }

                _allTransactions.Add(receipt.TransactionId, receipt);
                await _transactionManager.AddTransactionAsync(transaction);

                var prefix = await GetPrefixByHeightAsync(ChainId, receipt.Transaction.RefBlockNumber, _bestChainHash);
                CheckPrefixForOne(receipt, prefix, _bestChainHeight);
                AddToRespectiveCurrentCollection(receipt);
                if (receipt.RefBlockStatus == RefBlockStatus.RefBlockValid)
                {
                    await LocalEventBus.PublishAsync(new TransactionAcceptedEvent()
                    {
                        ChainId = eventData.ChainId,
                        Transaction = transaction
                    });
                }
            }
        }

        public async Task HandleBlockAcceptedAsync(BlockAcceptedEvent eventData)
        {
            if (ChainId != eventData.ChainId)
            {
                return;
            }

            var block = await _blockchainService.GetBlockByHashAsync(eventData.ChainId,
                eventData.BlockHeader.GetHash());
            foreach (var txId in block.Body.Transactions)
            {
                _allTransactions.Remove(txId);
            }
        }

        public async Task HandleBestChainFoundAsync(BestChainFoundEventData eventData)
        {
            if (ChainId != eventData.ChainId)
            {
                return;
            }

            var heights = _allTransactions.Select(kv => kv.Value.Transaction.RefBlockNumber).Distinct();
            var prefixes = await GetPrefixesByHeightAsync(eventData.ChainId, heights, eventData.BlockHash);
            ResetCurrentCollections();
            foreach (var kv in _allTransactions)
            {
                var prefix = prefixes[kv.Value.Transaction.RefBlockNumber];
                CheckPrefixForOne(kv.Value, prefix, _bestChainHeight);
                AddToRespectiveCurrentCollection(kv.Value);
            }

            _bestChainHash = eventData.BlockHash;
            _bestChainHeight = eventData.BlockHeight;
        }

        public async Task HandleNewIrreversibleBlockFoundAsync(NewIrreversibleBlockFoundEvent eventData)
        {
            if (ChainId != eventData.ChainId)
            {
                return;
            }

            foreach (var txIds in _expiredByExpiryBlock.Where(kv => kv.Key <= eventData.BlockHeight))
            {
                foreach (var txId in txIds.Value.Keys)
                {
                    _allTransactions.Remove(txId);
                }
            }

            await Task.CompletedTask;
        }

        #endregion
    }
}