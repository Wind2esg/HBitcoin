﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ConcurrentCollections;
using HBitcoin.Helpers;
using HBitcoin.KeyManagement;
using HBitcoin.MemPool;
using HBitcoin.Models;
using NBitcoin;
using NBitcoin.Protocol;
using NBitcoin.Protocol.Behaviors;
using Newtonsoft.Json.Linq;
using QBitNinja.Client;
using QBitNinja.Client.Models;
using Stratis.Bitcoin.BlockPulling;

namespace HBitcoin.FullBlockSpv
{
	public static class WalletJob
	{
		#region MembersAndProperties

		public static Safe Safe { get; private set; }
		public static bool TracksDefaultSafe { get; private set; }
		public static ConcurrentHashSet<SafeAccount> SafeAccounts { get; private set; }

		private static QBitNinjaClient _qBitClient;
		private static HttpClient _httpClient;

		private static Height _creationHeight;
		public static Height CreationHeight
		{
			get
			{
				// it's enough to estimate once
				if(_creationHeight != Height.Unknown) return _creationHeight;
				else return _creationHeight = FindSafeCreationHeight();
			}
		}
		private static Height FindSafeCreationHeight()
		{
			try
			{
				var currTip = HeaderChain.Tip;
				var currTime = currTip.Header.BlockTime;

				// the chain didn't catch up yet
				if(currTime < Safe.EarliestPossibleCreationTime)
					return Height.Unknown;

				// the chain didn't catch up yet
				if(currTime < Safe.CreationTime)
					return Height.Unknown;

				while(currTime > Safe.CreationTime)
				{
					currTip = currTip.Previous;
					currTime = currTip.Header.BlockTime;
				}

				// when the current tip time is lower than the creation time of the safe let's estimate that to be the creation height
				return new Height(currTip.Height);
			}
			catch
			{
				return Height.Unknown;
			}
		}

		public static Height BestHeight => HeaderChain.Height < Tracker.BestHeight ? Height.Unknown : Tracker.BestHeight;
		public static event EventHandler BestHeightChanged;
		private static void OnBestHeightChanged() => BestHeightChanged?.Invoke(null, EventArgs.Empty);

		public static int ConnectedNodeCount
		{
			get
			{
				if(Nodes == null) return 0;
				return Nodes.ConnectedNodes.Count;
			}
		}
		public static int MaxConnectedNodeCount
		{
			get
			{
				if(Nodes == null) return 0;
				return Nodes.MaximumNodeConnection;
			}
		}
		public static event EventHandler ConnectedNodeCountChanged;
		private static void OnConnectedNodeCountChanged() => ConnectedNodeCountChanged?.Invoke(null, EventArgs.Empty);

		private static WalletState _state;
		public static WalletState State
		{
			get { return _state; }
			private set
			{
				if(_state == value) return;
				_state = value;
				OnStateChanged();
			}
		}
		public static event EventHandler StateChanged;
		private static void OnStateChanged() => StateChanged?.Invoke(null, EventArgs.Empty);
		public static bool ChainsInSync => Tracker.BestHeight == HeaderChain.Height;

		private static readonly SemaphoreSlim SemaphoreSave = new SemaphoreSlim(1, 1);
		private static NodeConnectionParameters _connectionParameters;
		public static NodesGroup Nodes { get; private set; }
		private static LookaheadBlockPuller BlockPuller;

		private const string WorkFolderPath = "FullBlockSpvData";
		private static string _addressManagerFilePath => Path.Combine(WorkFolderPath, $"AddressManager{Safe.Network}.dat");
		private static string _headerChainFilePath => Path.Combine(WorkFolderPath, $"HeaderChain{Safe.Network}.dat");
		private static string _trackerFolderPath => Path.Combine(WorkFolderPath, Safe.UniqueId);

		private static Tracker _tracker;
		public static Tracker Tracker => GetTrackerAsync().Result;
		// This async getter is for clean exception handling
		private static async Task<Tracker> GetTrackerAsync()
		{
			// if already in memory return it
			if (_tracker != null) return _tracker;

			// else load it
			_tracker = new Tracker(Safe.Network);
			try
			{
				await _tracker.LoadAsync(_trackerFolderPath).ConfigureAwait(false);
			}
			catch
			{
				// Sync blockchain:
				_tracker = new Tracker(Safe.Network);
			}

			return _tracker;
		}

		private static AddressManager AddressManager
		{
			get
			{
				if (_connectionParameters != null)
				{
					foreach (var behavior in _connectionParameters.TemplateBehaviors)
					{
						var addressManagerBehavior = behavior as AddressManagerBehavior;
						if (addressManagerBehavior != null)
							return addressManagerBehavior.AddressManager;
					}
				}
				SemaphoreSave.Wait();
				try
				{
					return AddressManager.LoadPeerFile(_addressManagerFilePath);
				}
				catch
				{
					return new AddressManager();
				}
				finally
				{
					SemaphoreSave.Release();
				}
			}
		}

		private static ConcurrentChain HeaderChain
		{
			get
			{
				if (_connectionParameters != null)
					foreach (var behavior in _connectionParameters.TemplateBehaviors)
					{
						var chainBehavior = behavior as ChainBehavior;
						if (chainBehavior != null)
							return chainBehavior.Chain;
					}
				var chain = new ConcurrentChain(Safe.Network);
				SemaphoreSave.Wait();
				try
				{
					chain.Load(File.ReadAllBytes(_headerChainFilePath));
				}
				catch
				{
					// ignored
				}
				finally
				{
					SemaphoreSave.Release();
				}

				return chain;
			}
		}

		public static Network Network => Safe.Network;

		#endregion

		public static void Init(Safe safeToTrack, HttpClientHandler handler = null, bool trackDefaultSafe = true, params SafeAccount[] accountsToTrack)
		{
			_creationHeight = Height.Unknown;
			_tracker = null;

			Safe = safeToTrack;

			_qBitClient = new QBitNinjaClient(safeToTrack.Network);
			_httpClient = new HttpClient();
			if (handler != null)
			{
				_qBitClient.SetHttpMessageHandler(handler);
				_httpClient = new HttpClient(handler);
			}

			if (accountsToTrack == null || !accountsToTrack.Any())
			{
				SafeAccounts = new ConcurrentHashSet<SafeAccount>();
			}
			else SafeAccounts = new ConcurrentHashSet<SafeAccount>(accountsToTrack);

			TracksDefaultSafe = trackDefaultSafe;

			State = WalletState.NotStarted;

			Directory.CreateDirectory(WorkFolderPath);

			Tracker.TrackedTransactions.CollectionChanged += delegate
			{
				UpdateSafeTracking();
			};

			_connectionParameters = new NodeConnectionParameters();
			//So we find nodes faster
			_connectionParameters.TemplateBehaviors.Add(new AddressManagerBehavior(AddressManager));
			//So we don't have to load the chain each time we start
			_connectionParameters.TemplateBehaviors.Add(new ChainBehavior(HeaderChain));

			UpdateSafeTracking();

			Nodes = new NodesGroup(Safe.Network, _connectionParameters,
				new NodeRequirement
				{
					RequiredServices = NodeServices.Network,
					MinVersion = ProtocolVersion.SENDHEADERS_VERSION
				});
			var bp = new NodesBlockPuller(HeaderChain, Nodes.ConnectedNodes);
			_connectionParameters.TemplateBehaviors.Add(new NodesBlockPuller.NodesBlockPullerBehavior(bp));
			Nodes.NodeConnectionParameters = _connectionParameters;
			BlockPuller = (LookaheadBlockPuller)bp;

			MemPoolJob.Synced += delegate
			{
				State = WalletState.Synced;
			};

			MemPoolJob.NewTransaction += MemPoolJob_NewTransaction;

			Nodes.ConnectedNodes.Removed += delegate { OnConnectedNodeCountChanged(); };
			Nodes.ConnectedNodes.Added += delegate { OnConnectedNodeCountChanged(); };

			Tracker.BestHeightChanged += delegate { OnBestHeightChanged(); };
		}

		public static async Task StartAsync(CancellationToken ctsToken)
		{
			Nodes.Connect();

			var tasks = new ConcurrentHashSet<Task>
			{
				PeriodicSaveAsync(TimeSpan.FromMinutes(3), ctsToken),
				BlockPullerJobAsync(ctsToken),
				MemPoolJob.StartAsync(ctsToken)
			};

			State = WalletState.SyncingBlocks;
			await Task.WhenAll(tasks).ConfigureAwait(false);

			State = WalletState.NotStarted;
			await SaveAllChangedAsync().ConfigureAwait(false);
			Nodes.Dispose();
		}

		#region SafeTracking

		// BIP44 specifies default 20, altough we don't use BIP44, let's be somewhat consistent
		public static int MaxCleanAddressCount { get; set; } = 20;
	    private static void UpdateSafeTracking()
		{
			UpdateSafeTrackingByHdPathType(HdPathType.Receive);
			UpdateSafeTrackingByHdPathType(HdPathType.Change);
			UpdateSafeTrackingByHdPathType(HdPathType.NonHardened);
		}

		private static void UpdateSafeTrackingByHdPathType(HdPathType hdPathType)
		{
			if (TracksDefaultSafe) UpdateSafeTrackingByPath(hdPathType);

			foreach (var acc in SafeAccounts)
			{
				UpdateSafeTrackingByPath(hdPathType, acc);
			}
		}

		private static void UpdateSafeTrackingByPath(HdPathType hdPathType, SafeAccount account = null)
		{
			int i = 0;
			var cleanCount = 0;
			while (true)
			{
				Script scriptPubkey = account == null ? Safe.GetAddress(i, hdPathType).ScriptPubKey : Safe.GetAddress(i, hdPathType, account).ScriptPubKey;

				Tracker.TrackedScriptPubKeys.Add(scriptPubkey);

				// if clean elevate cleancount and if max reached don't look for more
				if(Tracker.IsClean(scriptPubkey))
				{
					cleanCount++;
					if (cleanCount > MaxCleanAddressCount) return;
				}

				i++;
			}
		}

		#endregion

		#region Misc

		/// <summary>
		/// 
		/// </summary>
		/// <param name="account">if null then default safe, if doesn't contain, then exception</param>
		/// <returns></returns>
		public static IEnumerable<SafeHistoryRecord> GetSafeHistory(SafeAccount account = null)
		{
			AssertAccount(account);

			var safeHistory = new HashSet<SafeHistoryRecord>();

			var transactions = GetAllChainAndMemPoolTransactionsBySafeAccount(account);
			var scriptPubKeys = GetTrackedScriptPubKeysBySafeAccount(account);

			foreach (SmartTransaction transaction in transactions)
			{
				SafeHistoryRecord record = new SafeHistoryRecord();
				record.TransactionId = transaction.GetHash();
				record.BlockHeight = transaction.Height;
				// todo: the mempool could note when it seen the transaction the first time
				record.TimeStamp = !transaction.Confirmed
					? DateTimeOffset.UtcNow
					: HeaderChain.GetBlock(transaction.Height).Header.BlockTime;

				record.Amount = Money.Zero; //for now

				// how much came to our scriptpubkeys
				foreach (var output in transaction.Transaction.Outputs)
				{
					if (scriptPubKeys.Contains(output.ScriptPubKey))
						record.Amount += output.Value;
				}

				foreach (var input in transaction.Transaction.Inputs)
				{
					// do we have the input?
					SmartTransaction inputTransaction = transactions.FirstOrDefault(x => x.GetHash() == input.PrevOut.Hash);
					if (default(SmartTransaction) != inputTransaction)
					{
						// if yes then deduct from amount (bitcoin output cannot be partially spent)
						var prevOutput = inputTransaction.Transaction.Outputs[input.PrevOut.N];
						if (scriptPubKeys.Contains(prevOutput.ScriptPubKey))
						{
							record.Amount -= prevOutput.Value;
						}
					}
					// if no then whatever
				}

				safeHistory.Add(record);
			}

			return safeHistory.ToList().OrderBy(x => x.TimeStamp);
		}

		private static void AssertAccount(SafeAccount account)
		{
			if (account == null)
			{
				if (!TracksDefaultSafe)
					throw new NotSupportedException($"{nameof(TracksDefaultSafe)} cannot be {TracksDefaultSafe}");
			}
			else
			{
				if (!SafeAccounts.Any(x => x.Id == account.Id))
					throw new NotSupportedException($"{nameof(SafeAccounts)} does not contain the provided {nameof(account)}");
			}
		}

		public static HashSet<SmartTransaction> GetAllChainAndMemPoolTransactionsBySafeAccount(SafeAccount account = null)
		{
			HashSet<Script> trackedScriptPubkeys = GetTrackedScriptPubKeysBySafeAccount(account);
			var foundTransactions = new HashSet<SmartTransaction>();

			foreach (var spk in trackedScriptPubkeys)
			{
				HashSet<SmartTransaction> rec;
				HashSet<SmartTransaction> spent;

				if (TryFindAllChainAndMemPoolTransactions(spk, out rec, out spent))
				{
					foreach (var tx in rec)
					{
						foundTransactions.Add(tx);
					}
					foreach (var tx in spent)
					{
						foundTransactions.Add(tx);
					}
				}
			}

			return foundTransactions;
		}

		public static HashSet<Script> GetTrackedScriptPubKeysBySafeAccount(SafeAccount account = null)
		{
			var maxTracked = Tracker.TrackedScriptPubKeys.Count;
			var allPossiblyTrackedAddresses = new HashSet<BitcoinAddress>();
			foreach (var address in Safe.GetFirstNAddresses(maxTracked, HdPathType.Receive, account))
			{
				allPossiblyTrackedAddresses.Add(address);
			}
			foreach (var address in Safe.GetFirstNAddresses(maxTracked, HdPathType.Change, account))
			{
				allPossiblyTrackedAddresses.Add(address);
			}
			foreach (var address in Safe.GetFirstNAddresses(maxTracked, HdPathType.NonHardened, account))
			{
				allPossiblyTrackedAddresses.Add(address);
			}

			var actuallyTrackedScriptPubKeys = new HashSet<Script>();
			foreach (var address in allPossiblyTrackedAddresses)
			{
				if (Tracker.TrackedScriptPubKeys.Any(x => x == address.ScriptPubKey))
					actuallyTrackedScriptPubKeys.Add(address.ScriptPubKey);
			}

			return actuallyTrackedScriptPubKeys;
		}

		private static void MemPoolJob_NewTransaction(object sender, NewTransactionEventArgs e)
		{
			if (
				Tracker.ProcessTransaction(new SmartTransaction(e.Transaction, Height.MemPool)))
			{
				UpdateSafeTracking();
			}
		}

		/// <summary>
		/// 
		/// </summary>
		/// <param name="scriptPubKey"></param>
		/// <param name="receivedTransactions">int: block height</param>
		/// <param name="spentTransactions">int: block height</param>
		/// <returns></returns>
		public static bool TryFindAllChainAndMemPoolTransactions(Script scriptPubKey, out HashSet<SmartTransaction> receivedTransactions, out HashSet<SmartTransaction> spentTransactions)
	    {
			var found = false;
			receivedTransactions = new HashSet<SmartTransaction>();
			spentTransactions = new HashSet<SmartTransaction>();
			
			foreach (var tx in Tracker.TrackedTransactions)
			{
				// if already has that tx continue
				if (receivedTransactions.Any(x => x.GetHash() == tx.GetHash()))
					continue;

				foreach (var output in tx.Transaction.Outputs)
				{
					if (output.ScriptPubKey.Equals(scriptPubKey))
					{
						receivedTransactions.Add(tx);
						found = true;
					}
				}
			}

		    if(found)
		    {
			    foreach(var tx in Tracker.TrackedTransactions)
			    {
				    // if already has that tx continue
				    if(spentTransactions.Any(x => x.GetHash() == tx.GetHash()))
					    continue;

				    foreach(var input in tx.Transaction.Inputs)
				    {
					    if(receivedTransactions.Select(x => x.GetHash()).Contains(input.PrevOut.Hash))
					    {
						    spentTransactions.Add(tx);
						    found = true;
					    }
				    }
			    }
		    }

		    return found;
		}

		public static bool TryGetHeader(Height height, out ChainedBlock creationHeader)
		{
			creationHeader = null;
			try
			{
				if (_connectionParameters == null)
					return false;

				creationHeader = HeaderChain.GetBlock(height);

				if (creationHeader == null)
					return false;
				else return true;
			}
			catch
			{
				return false;
			}
		}

		public static bool TryGetHeaderHeight(out Height height)
		{
			height = Height.Unknown;
			try
			{
				if (_connectionParameters == null)
					return false;

				height = new Height(HeaderChain.Height);
				return true;
			}
			catch
			{
				return false;
			}
		}

		#endregion

		#region BlockPulling
		private static async Task BlockPullerJobAsync(CancellationToken ctsToken)
		{
			const int currTimeoutDownSec = 360;
			while(true)
		    {
			    try
			    {
				    if(ctsToken.IsCancellationRequested)
				    {
					    return;
				    }

				    // the headerchain didn't catch up to the creationheight yet
				    if(CreationHeight == Height.Unknown)
				    {
					    await Task.Delay(1000, ctsToken).ContinueWith(tsk => { }).ConfigureAwait(false);
					    continue;
				    }

				    if(HeaderChain.Height < CreationHeight)
				    {
					    await Task.Delay(1000, ctsToken).ContinueWith(tsk => { }).ConfigureAwait(false);
					    continue;
				    }

				    Height height;
				    if(Tracker.BlockCount == 0)
				    {
					    height = CreationHeight;
				    }
				    else
					{
						int headerChainHeight = HeaderChain.Height;
						Height trackerBestHeight = Tracker.BestHeight;
						Height unprocessedBlockBestHeight = Tracker.UnprocessedBlockBuffer.BestHeight;
						if (headerChainHeight <= trackerBestHeight)
					    {
						    await Task.Delay(100, ctsToken).ContinueWith(tsk => { }).ConfigureAwait(false);
						    continue;
					    }
					    else if(unprocessedBlockBestHeight.Type == HeightType.Chain && (headerChainHeight <= unprocessedBlockBestHeight))
					    {
						    await Task.Delay(100, ctsToken).ContinueWith(tsk => { }).ConfigureAwait(false);
						    continue;
					    }
					    else if(Tracker.UnprocessedBlockBuffer.Full)
					    {
						    await Task.Delay(100, ctsToken).ContinueWith(tsk => { }).ConfigureAwait(false);
						    continue;
					    }
					    else
						{
							int relevant = unprocessedBlockBestHeight.Type == HeightType.Chain 
								? unprocessedBlockBestHeight.Value : 0;

						    if(trackerBestHeight.Type != HeightType.Chain)
						    {
								await Task.Delay(100, ctsToken).ContinueWith(tsk => { }).ConfigureAwait(false);
								continue;
							}

							height = new Height(
								Math.Max(trackerBestHeight.Value, relevant)
								+ 1);
					    }
				    }

					var chainedBlock = HeaderChain.GetBlock(height);
				    BlockPuller.SetLocation(new ChainedBlock(chainedBlock.Previous.Header, chainedBlock.Previous.Height));
				    Block block = null;
				    CancellationTokenSource ctsBlockDownload = CancellationTokenSource.CreateLinkedTokenSource(
					    new CancellationTokenSource(TimeSpan.FromSeconds(currTimeoutDownSec)).Token,
					    ctsToken);
				    var blockDownloadTask = Task.Run(() => BlockPuller.NextBlock(ctsBlockDownload.Token));
					block = await blockDownloadTask.ContinueWith(t =>
					{
						if (ctsToken.IsCancellationRequested) return null;
						if (t.IsCanceled || t.IsFaulted)
						{
							Nodes.Purge("no reason");
							Debug.WriteLine(
								$"Purging nodes, reason: couldn't download block in {currTimeoutDownSec} seconds.");
							return null;
						}
						return t.Result;
					}).ConfigureAwait(false);
					
					if (ctsToken.IsCancellationRequested) return;
					if (blockDownloadTask.IsCanceled || blockDownloadTask.IsFaulted)
						continue;

				    if(block == null) // then reorg happened
				    {
					    Reorg();
					    continue;
				    }

				    Tracker.AddOrReplaceBlock(new Height(chainedBlock.Height), block);
			    }
				catch (Exception ex)
				{
					Debug.WriteLine($"Ignoring {nameof(BlockPullerJobAsync)} exception:");
					Debug.WriteLine(ex);
				}
			}
		}

	    private static void Reorg()
		{
			HeaderChain.SetTip(HeaderChain.Tip.Previous);
			Tracker.ReorgOne();
		}
		#endregion

		#region Saving
		private static async Task PeriodicSaveAsync(TimeSpan delay, CancellationToken ctsToken)
		{
			while (true)
			{
				try
				{
					if (ctsToken.IsCancellationRequested) return;

					await SaveAllChangedAsync().ConfigureAwait(false);

					await Task.Delay(delay, ctsToken).ContinueWith(tsk => { }).ConfigureAwait(false);
				}
				catch(Exception ex)
				{
					Debug.WriteLine($"Ignoring {nameof(PeriodicSaveAsync)} exception:");
					Debug.WriteLine(ex);
				}
			}
		}

	    private static Height _savedHeaderHeight = Height.Unknown;
	    private static Height _savedTrackingHeight = Height.Unknown;

	    private static async Task SaveAllChangedAsync()
	    {
		    await SemaphoreSave.WaitAsync().ConfigureAwait(false);
			try
		    {
			    AddressManager.SavePeerFile(_addressManagerFilePath, Safe.Network);
				Debug.WriteLine($"Saved {nameof(AddressManager)}");

				if (_connectionParameters != null)
			    {
				    var headerHeight = new Height(HeaderChain.Height);
					if(_savedHeaderHeight == Height.Unknown || headerHeight > _savedHeaderHeight)
				    {
					    SaveHeaderChain();
						Debug.WriteLine($"Saved {nameof(HeaderChain)} at height: {headerHeight}");
					    _savedHeaderHeight = headerHeight;
				    }
			    }
		    }
		    finally
		    {
			    SemaphoreSave.Release();
		    }

		    var trackingHeight = BestHeight;
		    if(trackingHeight.Type == HeightType.Chain
				&& (_savedTrackingHeight == Height.Unknown
					|| trackingHeight > _savedTrackingHeight))
		    {
			    await Tracker.SaveAsync(_trackerFolderPath).ConfigureAwait(false);
			    Debug.WriteLine($"Saved {nameof(Tracker)} at height: {trackingHeight}");
			    _savedTrackingHeight = trackingHeight;
		    }
	    }

	    private static void SaveHeaderChain()
		{
			using (var fs = File.Open(_headerChainFilePath, FileMode.Create))
			{
				HeaderChain.WriteTo(fs);
			}
		}
		#endregion

		#region TransactionSending

		/// <summary>
		/// 
		/// </summary>
		/// <param name="scriptPubKeyToSpend"></param>
		/// <param name="amount">If Money.Zero then spend all available amount</param>
		/// <param name="feeType"></param>
		/// <param name="account"></param>
		/// <param name="allowUnconfirmed">Allow to spend unconfirmed transactions, if necessary</param>
		/// <returns></returns>
		public static async Task<BuildTransactionResult> BuildTransactionAsync(Script scriptPubKeyToSpend, Money amount, FeeType feeType, SafeAccount account = null, bool allowUnconfirmed = false)
		{
			try
			{
				AssertAccount(account);

				// 1. Get the script pubkey of the change.
				Debug.WriteLine("Select change address...");
				Script changeScriptPubKey;
				int i = 0;
				while (true)
				{
					Script scriptPubkey = account == null ? Safe.GetAddress(i, HdPathType.Change).ScriptPubKey : Safe.GetAddress(i, HdPathType.Change, account).ScriptPubKey;
					if (Tracker.IsClean(scriptPubkey))
					{
						changeScriptPubKey = scriptPubkey;
						break;
					}
					i++;
				}

				// 2. Find all coins I can spend from the account
				// 3. How much money we can spend?
				Debug.WriteLine("Calculating available amount...");
				IDictionary<Coin, bool> unspentCoins;
				AvailableAmount balance = GetBalance(out unspentCoins, account, allowUnconfirmed);
				var availableAmount = balance.Confirmed;
				var unconfirmedAvailableAmount = balance.Unconfirmed;
				Debug.WriteLine($"Available amount: {availableAmount}");

				BuildTransactionResult result = new BuildTransactionResult();

				// 4. Get and calculate fee
				Debug.WriteLine("Calculating dynamic transaction fee...");
				Money feePerBytes = null;
				try
				{
					feePerBytes = await QueryFeePerBytesAsync(feeType).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					Debug.WriteLine(ex.Message);
					return new BuildTransactionResult
					{
						Success = false,
						FailingReason = $"Couldn't calculate transaction fee. Reason:{Environment.NewLine}{ex}"
					};
				}

				bool spendAll = amount == Money.Zero;
				int inNum;
				if(spendAll)
				{
					inNum = unspentCoins.Count;
				}
				else
				{
					const int expectedMinTxSize = 1 * 148 + 2 * 34 + 10 - 1;
					try
					{
						inNum = SelectCoinsToSpend(unspentCoins, amount + feePerBytes * expectedMinTxSize).Count;
					}
					catch(InsufficientBalanceException)
					{
						return new BuildTransactionResult
						{
							Success = false,
							FailingReason = "Not enough funds"
						};
					}
				}

				const int outNum = 2; // 1 address to send + 1 for change
				var estimatedTxSize = inNum * 148 + outNum * 34 + 10 + inNum; // http://bitcoin.stackexchange.com/questions/1195/how-to-calculate-transaction-size-before-sending
				Debug.WriteLine($"Estimated tx size: {estimatedTxSize} bytes");
				Money fee = feePerBytes * estimatedTxSize;
				Debug.WriteLine($"Fee: {fee.ToDecimal(MoneyUnit.BTC):0.#############################}btc");
				result.Fee = fee;

				// 5. How much to spend?
				Money amountToSend = null;
				if (spendAll)
				{
					amountToSend = availableAmount;
					amountToSend -= fee;
				}
				else
				{
					amountToSend = amount;
				}

				// 6. Do some checks
				if (amountToSend < Money.Zero || availableAmount < amountToSend + fee)
					return new BuildTransactionResult
					{
						Success = false,
						FailingReason = "Not enough funds"
					};
				
				decimal feePc = (100 * fee.ToDecimal(MoneyUnit.BTC)) / amountToSend.ToDecimal(MoneyUnit.BTC);
				result.FeePercentOfSent = feePc;
				if (feePc > 1)
				{
					Debug.WriteLine("");
					Debug.WriteLine($"The transaction fee is {feePc:0.#}% of your transaction amount.");
					Debug.WriteLine($"Sending:\t {amountToSend.ToDecimal(MoneyUnit.BTC):0.#############################}btc");
					Debug.WriteLine($"Fee:\t\t {fee.ToDecimal(MoneyUnit.BTC):0.#############################}btc");
				}

				var confirmedAvailableAmount = availableAmount - unconfirmedAvailableAmount;
				var totalOutAmount = amountToSend + fee;
				if (confirmedAvailableAmount < totalOutAmount)
				{
					var unconfirmedToSend = totalOutAmount - confirmedAvailableAmount;
					Debug.WriteLine("");
					Debug.WriteLine($"In order to complete this transaction you have to spend {unconfirmedToSend.ToDecimal(MoneyUnit.BTC):0.#############################} unconfirmed btc.");
					result.SpendsUnconfirmed = true;
				}

				// 7. Select coins
				Debug.WriteLine("Selecting coins...");
				HashSet<Coin> coinsToSpend = SelectCoinsToSpend(unspentCoins, totalOutAmount);

				// 8. Get signing keys
				var signingKeys = new HashSet<ISecret>();
				foreach (var coin in coinsToSpend)
				{
					var signingKey = Safe.FindPrivateKey(coin.ScriptPubKey.GetDestinationAddress(Safe.Network), Tracker.TrackedScriptPubKeys.Count, account);
					signingKeys.Add(signingKey);
				}

				// 9. Build the transaction
				Debug.WriteLine("Signing transaction...");
				var builder = new TransactionBuilder();
				var tx = builder
					.AddCoins(coinsToSpend)
					.AddKeys(signingKeys.ToArray())
					.Send(scriptPubKeyToSpend, amountToSend)
					.SetChange(changeScriptPubKey)
					.SendFees(fee)
					.BuildTransaction(true);

				if(!builder.Verify(tx))
					return new BuildTransactionResult
					{
						Success = false,
						FailingReason = "Couldn't build the transaction"
					};

				result.Transaction = tx;
				result.Success = true;
				return result;
			}
			catch (Exception ex)
			{
				return new BuildTransactionResult
				{
					Success = false,
					FailingReason = ex.ToString()
				};
			}
		}

		private static async Task<Money> QueryFeePerBytesAsync(FeeType feeType)
		{
			HttpResponseMessage response =
				await _httpClient.GetAsync(@"http://api.blockcypher.com/v1/btc/main", HttpCompletionOption.ResponseContentRead)
					.ConfigureAwait(false);

			var json = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
			int satoshiPerByteFee;
			if(feeType == FeeType.High)
			{
				satoshiPerByteFee = (int)(json.Value<decimal>("high_fee_per_kb") / 1024);
			}
			else if(feeType == FeeType.Medium)
			{
				satoshiPerByteFee = (int)(json.Value<decimal>("medium_fee_per_kb") / 1024);
			}
			else // if(feeType == FeeType.Low)
			{
				satoshiPerByteFee = (int)(json.Value<decimal>("low_fee_per_kb") / 1024);
			}
			var feePerBytes = new Money(satoshiPerByteFee, MoneyUnit.Satoshi);

			return feePerBytes;
		}

		private static HashSet<Coin> SelectCoinsToSpend(IDictionary<Coin, bool> unspentCoins, Money totalOutAmount)
		{
			var coinsToSpend = new HashSet<Coin>();
			var unspentConfirmedCoins = new List<Coin>();
			var unspentUnconfirmedCoins = new List<Coin>();
			foreach (var elem in unspentCoins)
				if (elem.Value) unspentConfirmedCoins.Add(elem.Key);
				else unspentUnconfirmedCoins.Add(elem.Key);

			bool haveEnough = SelectCoins(ref coinsToSpend, totalOutAmount, unspentConfirmedCoins);
			if (!haveEnough)
				haveEnough = SelectCoins(ref coinsToSpend, totalOutAmount, unspentUnconfirmedCoins);
			if (!haveEnough)
				throw new InsufficientBalanceException();

			return coinsToSpend;
		}
		private static bool SelectCoins(ref HashSet<Coin> coinsToSpend, Money totalOutAmount, IEnumerable<Coin> unspentCoins)
		{
			var haveEnough = false;
			foreach (var coin in unspentCoins.OrderByDescending(x => x.Amount))
			{
				coinsToSpend.Add(coin);
				// if doesn't reach amount, continue adding next coin
				if (coinsToSpend.Sum(x => x.Amount) < totalOutAmount) continue;

				haveEnough = true;
				break;
			}

			return haveEnough;
		}

		public static AvailableAmount GetBalance(out IDictionary<Coin, bool> unspentCoins, SafeAccount account = null, bool allowUnconfirmed = false)
		{
			// 1. Find all coins I can spend from the account
			Debug.WriteLine("Finding all unspent coins...");
			unspentCoins = GetUnspentCoins(account, allowUnconfirmed);

			// 2. How much money we can spend?
			var availableAmount = Money.Zero;
			var unconfirmedAvailableAmount = Money.Zero;
			foreach (var elem in unspentCoins)
			{
				// If can spend unconfirmed add all
				if (allowUnconfirmed)
				{
					availableAmount += elem.Key.Amount as Money;
					if (!elem.Value)
						unconfirmedAvailableAmount += elem.Key.Amount as Money;
				}
				// else only add confirmed ones
				else
				{
					if (elem.Value)
					{
						availableAmount += elem.Key.Amount as Money;
					}
				}
			}

			return new AvailableAmount
			{
				Confirmed = availableAmount,
				Unconfirmed = unconfirmedAvailableAmount
			};
		}

		public struct BuildTransactionResult
		{
			public bool Success;
			public string FailingReason;
			public Transaction Transaction;
			public bool SpendsUnconfirmed;
			public Money Fee;
			public decimal FeePercentOfSent;
		}
		public struct AvailableAmount
		{
			public Money Confirmed;
			public Money Unconfirmed;
		}

		/// <summary>
		/// Find all unspent transaction output of the account
		/// </summary>
		public static IDictionary<Coin, bool> GetUnspentCoins(SafeAccount account = null, bool allowUnconfirmed = false)
		{
			AssertAccount(account);

			var unspentCoins = new Dictionary<Coin, bool>();

			var trackedScriptPubkeys = GetTrackedScriptPubKeysBySafeAccount(account);

			// 1. Go through all the transactions and their outputs
			foreach(SmartTransaction tx in Tracker
				.TrackedTransactions
				.Where(x => 
				(allowUnconfirmed || x.Confirmed) 
				&& x.Height.Type != HeightType.Unknown))
			{
				foreach(var coin in tx.Transaction.Outputs.AsCoins())
				{
					// 2. Check if the coin comes with our account
					if(trackedScriptPubkeys.Contains(coin.ScriptPubKey))
					{
						// 3. Check if coin is unspent, if so add to our utxoSet
						if(IsUnspent(coin))
						{
							unspentCoins.Add(coin, tx.Confirmed);
						}
					}
				}
			}

			return unspentCoins;
		}

		private static bool IsUnspent(Coin coin) => Tracker
			.TrackedTransactions
			.Where(x => x.Height.Type == HeightType.Chain || x.Height.Type == HeightType.MemPool)
			.SelectMany(x => x.Transaction.Inputs)
			.All(txin => txin.PrevOut != coin.Outpoint);

		public static async Task<SendTransactionResult> SendTransactionAsync(Transaction tx)
		{
			Debug.WriteLine($"Transaction Id: {tx.GetHash()}");

			// QBit's success response is buggy so let's check manually, too
			BroadcastResponse broadcastResponse;
			var success = false;
			var tried = 0;
			const int maxTry = 7;
			do
			{
				tried++;
				Debug.WriteLine($"Try broadcasting transaction... ({tried})");
				broadcastResponse = await _qBitClient.Broadcast(tx).ConfigureAwait(false);
				var getTxResp = await _qBitClient.GetTransaction(tx.GetHash()).ConfigureAwait(false);
				if(getTxResp != null)
				{
					success = true;
					break;
				}
				else
				{
					await Task.Delay(3000).ConfigureAwait(false);
				}
			} while(tried < maxTry);

			if(!success)
			{
				if(broadcastResponse.Error != null)
				{
					// Try broadcasting with smartbit if QBit fails (QBit issue)
					if(broadcastResponse.Error.ErrorCode == RejectCode.INVALID && broadcastResponse.Error.Reason == "Unknown")
					{
						Debug.WriteLine("Try broadcasting transaction with smartbit...");

						var post = "https://testnet-api.smartbit.com.au/v1/blockchain/pushtx";
						if(Safe.Network == Network.Main)
							post = "https://api.smartbit.com.au/v1/blockchain/pushtx";

						var content = new StringContent(new JObject(new JProperty("hex", tx.ToHex())).ToString(), Encoding.UTF8,
							"application/json");
						var resp = await _httpClient.PostAsync(post, content).ConfigureAwait(false);
						var json = JObject.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
						if(json.Value<bool>("success"))
						{
							Debug.WriteLine("Transaction is successfully propagated on the network.");
							return new SendTransactionResult
							{
								Success = true
							};
						}
						else
						{
							Debug.WriteLine(
								$"Error code: {json["error"].Value<string>("code")} Reason: {json["error"].Value<string>("message")}");
						}
					}
					else
					{
						Debug.WriteLine($"Error code: {broadcastResponse.Error.ErrorCode} Reason: {broadcastResponse.Error.Reason}");
					}
				}
				Debug.WriteLine(
					"The transaction might not have been successfully broadcasted. Please check the Transaction ID in a block explorer.");
				return new SendTransactionResult
				{
					Success = false,
					FailingReason =
						"The transaction might not have been successfully broadcasted. Please check the Transaction ID in a block explorer."
				};
			}
			Debug.WriteLine("Transaction is successfully propagated on the network.");
			return new SendTransactionResult
			{
				Success = true
			};
		}

		public struct SendTransactionResult
		{
			public bool Success;
			public string FailingReason;
		}

		#endregion
	}
}
