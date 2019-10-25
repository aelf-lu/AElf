using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AElf.Kernel;
using AElf.Kernel.Blockchain.Application;
using AElf.Kernel.Blockchain.Domain;
using AElf.Kernel.Blockchain.Events;
using AElf.Kernel.SmartContractExecution.Application;
using AElf.Kernel.TransactionPool.Infrastructure;
using AElf.Types;

namespace AElf.OS
{
    public class MockTxHub : ITxHub
    {
        private readonly ITransactionManager _transactionManager;
        private readonly IBlockchainService _blockchainService;

        private readonly Dictionary<Hash, Transaction> _allTransactions =
            new Dictionary<Hash, Transaction>();

        private long _bestChainHeight = Constants.GenesisBlockHeight - 1;
        private Hash _bestChainHash = Hash.Empty;

        public MockTxHub(ITransactionManager transactionManager, IBlockchainService blockchainService)
        {
            _transactionManager = transactionManager;
            _blockchainService = blockchainService;
        }

        public async Task<ExecutableTransactionSet> GetExecutableTransactionSetAsync(int transactionCount = 0)
        {
            return new ExecutableTransactionSet
            {
                PreviousBlockHash = _bestChainHash,
                PreviousBlockHeight = _bestChainHeight,
                Transactions = _allTransactions.Values.ToList()
            };
        }

        public async Task HandleTransactionsReceivedAsync(TransactionsReceivedEvent eventData)
        {
            foreach (var transaction in eventData.Transactions)
            {
                _allTransactions.Add(transaction.GetHash(), transaction);
                await _transactionManager.AddTransactionAsync(transaction);
            }
        }

        public async Task HandleBlockAcceptedAsync(BlockAcceptedEvent eventData)
        {
            var block = await _blockchainService.GetBlockByHashAsync(eventData.BlockHeader.GetHash());
            CleanTransactions(block.Body.TransactionIds.ToList());
        }

        public async Task HandleBestChainFoundAsync(BestChainFoundEventData eventData)
        {
            _bestChainHeight = eventData.BlockHeight;
            _bestChainHash = eventData.BlockHash;
            await Task.CompletedTask;
        }

        public async Task HandleNewIrreversibleBlockFoundAsync(NewIrreversibleBlockFoundEvent eventData)
        {
            await Task.CompletedTask;
        }

        public async Task HandleUnexecutableTransactionsFoundAsync(UnexecutableTransactionsFoundEvent eventData)
        {
            CleanTransactions(eventData.Transactions);
            await Task.CompletedTask;
        }

        public async Task<QueuedTransaction> GetQueuedTransactionAsync(Hash transactionId)
        {
            if (!_allTransactions.TryGetValue(transactionId, out var transaction))
            {
                return null;
            }

            return new QueuedTransaction
            {
                TransactionId = transactionId,
                Transaction = transaction,
            };
        }

        public Task<int> GetAllTransactionCountAsync()
        {
            return Task.FromResult(_allTransactions.Count);
        }

        public Task<int> GetValidatedTransactionCountAsync()
        {
            return Task.FromResult(_allTransactions.Count);
        }

        private void CleanTransactions(IEnumerable<Hash> transactionIds)
        {
            foreach (var transactionId in transactionIds)
            {
                _allTransactions.Remove(transactionId, out _);
            }
        }
    }
}