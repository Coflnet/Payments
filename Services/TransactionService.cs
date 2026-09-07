using Coflnet.Payments.Models;
using System.Linq;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Coflnet.Payments.Services
{

    public class TransactionService
    {
        private const string ExpertMarketplaceAgreementHash =
            "9177b208e3226cd0974afdce79d4023520d69d65aaa34a8d86e04dd3e60f2401";
        private const string CreatorMarketplaceAgreementHash =
            "652e91d78ec3aa86dd1e7e33c1e1a81dc423c7ecc1b004466cae1733c9c4a280";
        private static readonly DateTimeOffset ExpertConfigRolloutAt = new(
            2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
        private ILogger<TransactionService> logger;
        private PaymentContext db;
        private UserService userService;
        private ITransactionEventProducer transactionEventProducer;
        private TransferSettings transferSettings { get; set; }
        private IRuleEngine ruleEngine;
        private readonly TimeProvider timeProvider;
        private readonly bool enforceServicePerformanceDeclaration;
        private readonly IConfiguration configuration;

        public TransactionService(
            ILogger<TransactionService> logger,
            PaymentContext context,
            UserService userService,
            ITransactionEventProducer transactionEventProducer,
            IConfiguration config,
            IRuleEngine ruleEngine,
            TimeProvider timeProvider = null)
        {
            this.logger = logger;
            db = context;
            this.userService = userService;
            this.transactionEventProducer = transactionEventProducer;
            transferSettings = config?.GetSection("TRANSFER").Get<TransferSettings>();
            this.ruleEngine = ruleEngine;
            this.timeProvider = timeProvider ?? TimeProvider.System;
            configuration = config;
            enforceServicePerformanceDeclaration = config?.GetValue<bool>(
                "LEGAL:ENFORCE_SERVICE_PERFORMANCE_DECLARATION") == true;
        }

        /// <summary>
        /// Adds a top up to some user
        /// </summary>
        /// <param name="productId">The product purchased</param>
        /// <param name="userId">The user doing the transaction</param>
        /// <param name="reference">External reference data</param>
        /// <param name="customAmount">Custom amount to add as topup, has to be higher than the product cost</param>
        /// <returns></returns>
        public async Task AddTopUp(int productId, string userId, string reference, long customAmount = 0)
        {
            var product = db.TopUpProducts.Where(p => p.Id == productId).FirstOrDefault();

            var changeamount = product.Cost;
            if (customAmount != 0)
                if (customAmount < product.Cost)
                    throw new ApiException("custom amount is to smal for product");
                else
                    changeamount = customAmount;
            await CreateTransactionInTransaction(product, userId, changeamount, reference);
        }

        /// <summary>
        /// Applies the cumulative refund reported for a top-up order. Only the
        /// newly refunded balance is deducted, making repeated and progressive
        /// partial-refund webhooks idempotent.
        /// </summary>
        /// <param name="reference">External order reference used for the original top-up.</param>
        /// <param name="totalAmount">Original charged amount in the currency's smallest unit.</param>
        /// <param name="refundedAmount">Cumulative refunded amount in the same unit.</param>
        /// <returns>
        /// The positive balance amount deducted by this call, zero when this
        /// refund was already applied, or <c>null</c> when the original top-up
        /// could not be found.
        /// </returns>
        public async Task<decimal?> ApplyTopUpRefund(string reference, int totalAmount, int refundedAmount)
        {
            if (string.IsNullOrWhiteSpace(reference))
                throw new ArgumentException("A refund reference is required", nameof(reference));
            if (totalAmount <= 0)
                throw new ArgumentOutOfRangeException(nameof(totalAmount), "The original charged amount must be positive");
            if (refundedAmount <= 0)
                throw new ArgumentOutOfRangeException(nameof(refundedAmount), "The cumulative refunded amount must be positive");

            decimal? appliedAmount = null;
            await WithTransactionAsync(async (tx, owns) =>
            {
                var original = await db.FiniteTransactions
                    .Include(t => t.User)
                    .Include(t => t.Product)
                    .Where(t => t.Reference == reference
                        && t.Amount > 0
                        && t.Product.Type.HasFlag(Product.ProductType.TOP_UP))
                    .OrderBy(t => t.Id)
                    .FirstOrDefaultAsync();

                if (original == null)
                    return;

                appliedAmount = 0;
                var boundedRefundAmount = Math.Min(refundedAmount, totalAmount);
                var targetRefund = boundedRefundAmount == totalAmount
                    ? original.Amount
                    : Math.Round(
                        original.Amount * boundedRefundAmount / totalAmount,
                        0,
                        MidpointRounding.AwayFromZero);

                var refundReferencePrefix = $"refund transaction {original.Id} amount ";
                var legacyFullRefundReference = $"revert transaction {original.Id}";
                var previousRefunds = await db.FiniteTransactions
                    .Where(t => t.User.Id == original.User.Id
                        && t.Product.Slug == "revert"
                        && (t.Reference.StartsWith(refundReferencePrefix)
                            || t.Reference == legacyFullRefundReference))
                    .ToListAsync();
                var alreadyRefunded = Math.Min(
                    original.Amount,
                    previousRefunds.Where(t => t.Amount < 0).Sum(t => -t.Amount));
                var refundDelta = targetRefund - alreadyRefunded;

                if (refundDelta <= 0)
                    return;

                var refundProduct = await GetProduct("revert");
                var refundEvent = await CreateTransaction(
                    refundProduct,
                    original.User,
                    -refundDelta,
                    refundReferencePrefix + boundedRefundAmount);
                await transactionEventProducer.ProduceEvent(refundEvent);
                appliedAmount = refundDelta;
            });

            return appliedAmount;
        }

        public async Task<IDbContextTransaction> StartDbTransaction()
        {
            return await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        }

        /// <summary>
        /// Acquire the current transaction if present or begin a new one.
        /// Returns the transaction and a bool indicating whether the caller owns it (and should commit/rollback).
        /// </summary>
        internal async Task<(IDbContextTransaction transaction, bool owns)> AcquireTransactionIfNoneAsync()
        {
            var current = db.Database.CurrentTransaction;
            if (current != null)
                return (current, false);
            var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            return (tx, true);
        }

        /// <summary>
        /// Executes an action inside the current transaction or a newly created one.
        /// If a new transaction is created (owns == true) the wrapper will commit on success
        /// and rollback on exception and will dispose the transaction. If the transaction
        /// already existed (owns == false) the wrapper will not commit/rollback/dispose it.
        /// </summary>
        public async Task WithTransactionAsync(Func<IDbContextTransaction, bool, Task> action, bool autoCommit = true)
        {
            var (tx, owns) = await AcquireTransactionIfNoneAsync();
            logger.LogDebug("WithTransactionAsync (void): acquired tx {TxHash} owns={Owns} current={CurrentHash}", tx?.GetHashCode(), owns, db.Database.CurrentTransaction?.GetHashCode());
            try
            {
                await action(tx, owns);
                if (owns && autoCommit)
                {
                    if (db.Database.CurrentTransaction == tx)
                    {
                        logger.LogDebug("WithTransactionAsync (void): attempting commit tx {TxHash}", tx?.GetHashCode());
                        try
                        {
                            await tx.CommitAsync();
                            logger.LogDebug("WithTransactionAsync (void): committed tx {TxHash}", tx?.GetHashCode());
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "WithTransactionAsync (void): commit failed for tx {TxHash}", tx?.GetHashCode());
                            throw;
                        }
                    }
                    else
                    {
                        logger.LogDebug("WithTransactionAsync (void): current ambient transaction differs from acquired tx {TxHash} current={CurrentHash}", tx?.GetHashCode(), db.Database.CurrentTransaction?.GetHashCode());
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "WithTransactionAsync (void): caught exception, owns={Owns}, tx={TxHash}", owns, tx?.GetHashCode());
                if (owns)
                {
                    if (db.Database.CurrentTransaction == tx)
                    {
                        logger.LogDebug("WithTransactionAsync (void): attempting rollback tx {TxHash}", tx?.GetHashCode());
                        try
                        {
                            await tx.RollbackAsync();
                            logger.LogDebug("WithTransactionAsync (void): rolled back tx {TxHash}", tx?.GetHashCode());
                        }
                        catch (Exception rex)
                        {
                            logger.LogWarning(rex, "WithTransactionAsync (void): rollback failed for tx {TxHash}", tx?.GetHashCode());
                        }
                    }
                    else
                    {
                        logger.LogDebug("WithTransactionAsync (void): cannot rollback because ambient transaction differs from acquired tx {TxHash} current={CurrentHash}", tx?.GetHashCode(), db.Database.CurrentTransaction?.GetHashCode());
                    }
                }
                throw;
            }
            finally
            {
                if (owns && tx != null)
                {
                    logger.LogDebug("WithTransactionAsync (void): disposing tx {TxHash}", tx?.GetHashCode());
                    try { await tx.DisposeAsync(); } catch (Exception dex) { logger.LogWarning(dex, "WithTransactionAsync (void): dispose failed for tx {TxHash}", tx?.GetHashCode()); }
                }
            }
        }

        public async Task<T> WithTransactionAsync<T>(Func<IDbContextTransaction, bool, Task<T>> action, bool autoCommit = true)
        {
            var (tx, owns) = await AcquireTransactionIfNoneAsync();
            logger.LogDebug("WithTransactionAsync<T>: acquired tx {TxHash} owns={Owns} current={CurrentHash}", tx?.GetHashCode(), owns, db.Database.CurrentTransaction?.GetHashCode());
            try
            {
                var result = await action(tx, owns);
                if (owns && autoCommit)
                {
                    if (db.Database.CurrentTransaction == tx)
                    {
                        logger.LogDebug("WithTransactionAsync<T>: attempting commit tx {TxHash}", tx?.GetHashCode());
                        try
                        {
                            await tx.CommitAsync();
                            logger.LogDebug("WithTransactionAsync<T>: committed tx {TxHash}", tx?.GetHashCode());
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "WithTransactionAsync<T>: commit failed for tx {TxHash}", tx?.GetHashCode());
                            throw;
                        }
                    }
                    else
                    {
                        logger.LogDebug("WithTransactionAsync<T>: current ambient transaction differs from acquired tx {TxHash} current={CurrentHash}", tx?.GetHashCode(), db.Database.CurrentTransaction?.GetHashCode());
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "WithTransactionAsync<T>: caught exception, owns={Owns}, tx={TxHash}", owns, tx?.GetHashCode());
                if (owns)
                {
                    if (db.Database.CurrentTransaction == tx)
                    {
                        logger.LogDebug("WithTransactionAsync<T>: attempting rollback tx {TxHash}", tx?.GetHashCode());
                        try
                        {
                            await tx.RollbackAsync();
                            logger.LogDebug("WithTransactionAsync<T>: rolled back tx {TxHash}", tx?.GetHashCode());
                        }
                        catch (Exception rex)
                        {
                            logger.LogWarning(rex, "WithTransactionAsync<T>: rollback failed for tx {TxHash}", tx?.GetHashCode());
                        }
                    }
                    else
                    {
                        logger.LogDebug("WithTransactionAsync<T>: cannot rollback because ambient transaction differs from acquired tx {TxHash} current={CurrentHash}", tx?.GetHashCode(), db.Database.CurrentTransaction?.GetHashCode());
                    }
                }
                throw;
            }
            finally
            {
                if (owns && tx != null)
                {
                    logger.LogDebug("WithTransactionAsync<T>: disposing tx {TxHash}", tx?.GetHashCode());
                    try { await tx.DisposeAsync(); } catch (Exception dex) { logger.LogWarning(dex, "WithTransactionAsync<T>: dispose failed for tx {TxHash}", tx?.GetHashCode()); }
                }
            }
        }


        /// <summary>
        /// Execute a custom topup that changes an users balance in some way
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="topup"></param>
        /// <returns></returns>
        public async Task AddCustomTopUp(string userId, CustomTopUp topup)
        {
            var product = db.TopUpProducts.Where(p => p.Slug == topup.ProductId && p.ProviderSlug == "custom").FirstOrDefault();
            if (product == null)
                throw new ApiException($"{topup.ProductId} is not a valid custom topup option ");
            var changeamount = product.Cost;
            // adjust amount if its valid
            if (topup.Amount != 0 && topup.Amount < product.Cost)
                if (product.Type.HasFlag(Product.ProductType.VARIABLE_PRICE))
                    changeamount = topup.Amount;
                else
                    logger.LogWarning($"Variable price is disabled for {topup.ProductId} but a value of {topup.Amount} was passed");
            await CreateTransactionInTransaction(product, userId, changeamount, topup.Reference);
        }

        public async Task CreateTransactionInTransaction(TopUpProduct product, string userId, decimal changeamount, string reference)
        {
            await WithTransactionAsync(async (tx, owns) =>
            {
                var user = db.Users.Where(u => u.ExternalId == userId).FirstOrDefault();
                if (user == null)
                    throw new ApiException("user doesn't exist");
                await CreateAndProduceTransaction(product, user, changeamount, reference, owns);
            });
        }
        public async Task CreateTransactionInTransaction(TopUpProduct product, User user, decimal changeamount, string reference)
        {
            await WithTransactionAsync(async (tx, owns) =>
            {
                user = await db.Users.Where(u => u.Id == user.Id).FirstOrDefaultAsync(); // reload user for transaction lock
                await CreateAndProduceTransaction(product, user, changeamount, reference, owns);
            });
        }

        private async Task CreateAndProduceTransaction(TopUpProduct product, User user, decimal changeamount, string reference, bool commitTransaction)
        {
            var transactionEvent = await CreateTransaction(product, user, changeamount, reference);
            await transactionEventProducer.ProduceEvent(transactionEvent);
        }

        public async Task<TransactionEvent> CreateTransaction(Product product, User user, decimal changeamount, string reference = "", long adjustedOwnerShipTime = 0)
        {
            var transaction = new FiniteTransaction()
            {
                Product = product,
                Amount = changeamount,
                Reference = reference,
                User = user
            };
            var exists = await db.FiniteTransactions.Where(f =>
                f.Product == product
                && f.User == user
                && f.Reference == reference).AnyAsync();
            if (exists)
                throw new DupplicateTransactionException();
            db.FiniteTransactions.Add(transaction);
            user.Balance += changeamount;
            if (user.Balance < 0 && product.Slug != "revert" && changeamount < 0)
                throw new InsufficientFundsException(changeamount, user.Balance);
            db.Update(user);
            await db.SaveChangesAsync();
            var transactionEvent = new TransactionEvent()
            {
                Amount = Decimal.ToDouble(changeamount),
                Id = transaction.Id,
                OwnedSeconds = adjustedOwnerShipTime == 0 ? product.OwnershipSeconds : adjustedOwnerShipTime,
                ProductId = product.Id,
                ProductSlug = product.Slug,
                Reference = reference,
                UserId = user.ExternalId,
                Timestamp = transaction.Timestamp,
                ProductType = product.Type
            };
            return transactionEvent;
        }

        /// <summary>
        /// Purchase a product 
        /// </summary>
        /// <param name="productSlug"></param>
        /// <param name="userId"></param>
        /// <param name="price"></param>
        /// <returns></returns>
        public async Task PurchaseProduct(string productSlug, string userId, decimal price = 0)
        {
            if (productSlug == "config-purchase")
                throw new ApiException(
                    "Expert Configs require the declared service-purchase checkout");
            PurchaseableProduct product = await GetProduct(productSlug);
            if (product.SlotCount > 0)
                throw new ApiException("slot packages must be purchased as services");
            if (!product.Type.HasFlag(PurchaseableProduct.ProductType.VARIABLE_PRICE))
                price = product.Cost;
            await WithTransactionAsync(async (tx, owns) =>
            {
                var user = await userService.GetOrCreate(userId);
                if (user.Owns.Where(p => p.Product == product && p.Expires > DateTime.UtcNow + TimeSpan.FromDays(3000)).Any())
                    throw new ApiException("already owned");
                if (user.AvailableBalance < price || price < 0)
                    throw new ApiException("insuficcient balance");

                var transactionEvent = await CreateTransaction(product, user, price * -1);
                user.Owns.Add(new OwnerShip() { Expires = DateTime.UtcNow.AddSeconds(product.OwnershipSeconds), Product = product, User = user });
                db.Update(user);
                await db.SaveChangesAsync();
                await transactionEventProducer.ProduceEvent(transactionEvent);
            });
        }

        public async Task<PurchaseableProduct> GetProduct(string productSlug)
        {
            var product = await db.Products.Where(p => p.Slug == productSlug).FirstOrDefaultAsync();
            if (product == null)
                throw new ApiException($"product {productSlug} could not be found ");
            if (product.Type.HasFlag(PurchaseableProduct.ProductType.DISABLED))
                throw new ApiException("product can't be purchased");
            return product;
        }

        public async Task PurchaseServie(string productSlug, string userId, int count, string reference)
        {
            Product dbProduct = await GetProduct(productSlug);
            await PurchaseService(productSlug, userId, count, reference, dbProduct);
        }

        public async Task PurchaseService(string productSlug, string userId, int count, string reference, Product dbProduct)
        {
            await PurchaseServiceWithDeclaration(
                productSlug,
                userId,
                count,
                reference,
                dbProduct,
                null);
        }

        public async Task PurchaseServiceDeclared(
            string productSlug,
            string userId,
            ServicePurchaseRequest request)
        {
            if (request == null)
                throw new ApiException("service purchase request is required");
            var product = await GetProduct(productSlug);
            await PurchaseServiceWithDeclaration(
                productSlug,
                userId,
                request.Count,
                request.Reference,
                product,
                request);
        }

        public async Task<ServicePurchaseQuote> GetServicePurchaseQuote(
            string productSlug,
            string userId,
            int count)
        {
            if (productSlug != "config-purchase")
                throw new ApiException("service purchase quote is not supported");
            RequireExpertConfigRollout(userId);
            if (count < 1 || count > 100)
                throw new ApiException("invalid service purchase count");
            var product = await GetProduct(productSlug);
            if (!product.Type.HasFlag(Product.ProductType.SERVICE))
                throw new ApiException("product is not a service");
            var user = await userService.GetOrCreate(userId);
            var adjusted = ruleEngine == null
                ? product
                : (await ruleEngine.GetAdjusted(product, user)).ModifiedProduct;
            return await Quote(user, adjusted.Cost * count);
        }

        private async Task PurchaseServiceWithDeclaration(
            string productSlug,
            string userId,
            int count,
            string reference,
            Product dbProduct,
            ServicePurchaseRequest request)
        {
            if (!dbProduct.Type.HasFlag(Product.ProductType.SERVICE))
                throw new ApiException("product is not a service");
            if (count < 1 || count > 100)
                throw new ApiException("invalid service purchase count");
            if (request != null && string.IsNullOrWhiteSpace(reference))
                throw new ApiException("purchase reference is required");
            if (request?.SlotIds != null && dbProduct.SlotCount == 0)
                throw new ApiException("only slot products can extend slots");

            await WithTransactionAsync(async (tx, owns) =>
            {
                var user = await userService.GetOrCreate(userId);
                var locale = NormalizeLocale(request?.Locale);
                if (request != null)
                {
                    if (!Guid.TryParse(request.RequestId, out _))
                        throw new ApiException(
                            "invalid_service_performance_declaration");
                    var existing = await db.ServicePerformanceDeclarations
                        .SingleOrDefaultAsync(item =>
                            item.RequestId == request.RequestId);
                    if (existing != null)
                    {
                        if (Matches(
                                existing,
                                userId,
                                dbProduct.Id,
                                productSlug,
                                count,
                                reference,
                                request,
                                locale))
                        {
                            if (request.SlotIds != null)
                            {
                                var grantedSlots = await db.TierSlotGrants
                                    .Where(g => g.Transaction.User.ExternalId == userId
                                        && g.Transaction.Reference == reference && g.Transaction.ProductId == dbProduct.Id)
                                    .Select(g => g.TierSlotId).ToArrayAsync();
                                if (!grantedSlots.Order().SequenceEqual(request.SlotIds.Order()))
                                    throw new ApiException("purchase reference already used for different slots");
                            }
                            await EnsureServicePurchaseConfirmation(existing);
                            return;
                        }
                        throw new ApiException(
                            "service declaration request id already used");
                    }
                }
                if (productSlug == "config-purchase")
                    RequireExpertConfigRollout(userId);

                var adjustedProduct = ruleEngine == null
                    ? dbProduct
                    : (await ruleEngine.GetAdjusted(dbProduct, user))
                        .ModifiedProduct;
                var now = timeProvider.GetUtcNow().UtcDateTime;
                var currentExpiry = await userService.GetLongest(
                    userId,
                    new() { productSlug });
                var startsAt = currentExpiry > now ? currentExpiry : now;
                var endsAt = startsAt.AddSeconds(
                    adjustedProduct.OwnershipSeconds * (dbProduct.SlotCount > 0 ? 1 : count));
                if (request?.SlotIds != null)
                {
                    var expiries = await db.TierSlots.Where(s => request.SlotIds.Contains(s.Id)
                        && s.UserId == user.Id).Select(s => s.Expires).ToArrayAsync();
                    if (expiries.Length > 0)
                    {
                        startsAt = expiries.Min() > now ? expiries.Min() : now;
                        endsAt = (expiries.Max() > now ? expiries.Max() : now)
                            .AddSeconds(adjustedProduct.OwnershipSeconds);
                    }
                }
                var price = adjustedProduct.Cost * count;
                var quote = request != null && productSlug == "config-purchase"
                    ? await Quote(user, price)
                    : null;
                if (quote != null)
                    ValidateOrderEvidence(request, quote);
                var declarationRequired = (IsPremium(productSlug)
                        || productSlug == "config-purchase")
                    && price > 0
                    && startsAt < now.AddDays(14);
                var requested = request?.ImmediatePerformanceRequested == true
                    && request.WithdrawalConsequenceAcknowledged;

                if (request != null
                    && request.ImmediatePerformanceRequested
                        != request.WithdrawalConsequenceAcknowledged)
                    throw new ApiException(
                        "invalid_service_performance_declaration");
                if (declarationRequired && !requested)
                {
                    if (enforceServicePerformanceDeclaration
                        || productSlug == "config-purchase")
                        throw new ApiException(
                            "service_performance_declaration_required");
                    logger.LogWarning(
                        "Early-performance declaration rollout would block paid Premium purchase for user {UserId}",
                        userId);
                }

                var legacyRolloutShadow = productSlug != "config-purchase"
                    && requested
                    && !enforceServicePerformanceDeclaration
                    && string.IsNullOrWhiteSpace(request.AgreementId)
                    && string.IsNullOrWhiteSpace(request.AgreementHash);
                ServicePerformanceDeclaration evidence = null;
                if (legacyRolloutShadow)
                {
                    logger.LogWarning(
                        "Legacy declared purchase omitted SkyCofl Agreement Root evidence for user {UserId}",
                        userId);
                }
                else if (requested)
                {
                    ValidateDeclaration(request);
                    evidence = new()
                    {
                        RequestId = request.RequestId,
                        UserId = userId,
                        ProductId = dbProduct.Id,
                        ProductSlug = productSlug,
                        Count = count,
                        PurchaseReference = reference,
                        CoinAmount = price,
                        StartsAtUtc = startsAt,
                        EndsAtUtc = endsAt,
                        DeclarationRequired = declarationRequired,
                        EarlyPerformanceRequested =
                            request.ImmediatePerformanceRequested,
                        WithdrawalConsequenceAcknowledged =
                            request.WithdrawalConsequenceAcknowledged,
                        Locale = locale,
                        DeclarationVersion = request.DeclarationVersion,
                        DeclarationText = request.DeclarationText,
                        DeclarationSha256 = request.DeclarationSha256,
                        AgreementId = request.AgreementId,
                        AgreementHash = request.AgreementHash,
                        WithdrawalVersion = request.WithdrawalVersion,
                        WithdrawalSha256 = request.WithdrawalSha256,
                        TaxCountry = request.TaxCountry,
                        VatRateBasisPoints = request.VatRateBasisPoints,
                        GrossEurCents = request.GrossEurCents,
                        VatEurCents = request.VatEurCents,
                        OrderDetailsJson = request.OrderDetailsJson,
                        CreatedAtUtc = now
                    };
                    db.ServicePerformanceDeclarations.Add(evidence);
                }

                var transactionEvent = await ExecuteServicePurchase(
                    productSlug,
                    userId,
                    count,
                    reference,
                    dbProduct,
                    tx,
                    user,
                    adjustedProduct,
                    owns,
                    request == null ? null : now,
                    slotIds: request?.SlotIds);
                if (evidence != null)
                {
                    await EnqueueServicePurchaseConfirmation(
                        transactionEvent,
                        evidence);
                    await db.SaveChangesAsync();
                }
            });
        }

        private async Task EnsureServicePurchaseConfirmation(
            ServicePerformanceDeclaration evidence)
        {
            var transactionEvent = await db.FiniteTransactions
                .AsNoTracking()
                .Where(item => item.User.ExternalId == evidence.UserId
                    && item.ProductId == evidence.ProductId
                    && item.Reference == evidence.PurchaseReference)
                .OrderByDescending(item => item.Id)
                .Select(item => new TransactionEvent
                {
                    Id = item.Id,
                    UserId = evidence.UserId,
                    ProductId = evidence.ProductId,
                    ProductSlug = evidence.ProductSlug,
                    Reference = evidence.PurchaseReference,
                    Timestamp = item.Timestamp
                })
                .FirstOrDefaultAsync();
            if (transactionEvent == null)
                throw new ApiException("service purchase transaction not found");

            await EnqueueServicePurchaseConfirmation(transactionEvent, evidence);
            await db.SaveChangesAsync();
        }

        private async Task EnqueueServicePurchaseConfirmation(
            TransactionEvent transactionEvent,
            ServicePerformanceDeclaration evidence)
        {
            const string provider = "coflcoins";
            const string confirmationType = "service_purchase";
            var providerTransactionId = transactionEvent.Id.ToString(
                CultureInfo.InvariantCulture);
            if (db.PaymentConfirmationOutbox.Local.Any(item =>
                    item.Provider == provider
                    && item.ProviderTransactionId == providerTransactionId
                    && item.ConfirmationType == confirmationType)
                || await db.PaymentConfirmationOutbox.AnyAsync(item =>
                    item.Provider == provider
                    && item.ProviderTransactionId == providerTransactionId
                    && item.ConfirmationType == confirmationType))
                return;

            var payment = new PaymentEvent
            {
                ProductId = evidence.ProductSlug,
                UserId = evidence.UserId,
                Currency = "CoflCoins",
                PaymentMethod = "CoflCoin balance",
                PaymentProvider = provider,
                PaymentProviderTransactionId = providerTransactionId,
                Timestamp = transactionEvent.Timestamp,
                ConfirmationType = confirmationType,
                CoinAmount = evidence.CoinAmount,
                ServiceStartsAtUtc = evidence.StartsAtUtc,
                ServiceEndsAtUtc = evidence.EndsAtUtc,
                DeclarationVersion = evidence.DeclarationVersion,
                DeclarationText = evidence.DeclarationText,
                LegalLocale = evidence.Locale,
                AgreementId = evidence.AgreementId,
                AgreementHash = evidence.AgreementHash,
                WithdrawalVersion = evidence.WithdrawalVersion,
                WithdrawalSha256 = evidence.WithdrawalSha256,
                TaxCountry = evidence.TaxCountry,
                ConsumerRightsRegime = ConsumerRightsRegime(evidence.TaxCountry),
                VatRateBasisPoints = evidence.VatRateBasisPoints,
                GrossEurCents = evidence.GrossEurCents,
                VatEurCents = evidence.VatEurCents,
                OrderDetailsJson = evidence.OrderDetailsJson
            };
            db.PaymentConfirmationOutbox.Add(new()
            {
                Provider = provider,
                ProviderTransactionId = providerTransactionId,
                ConfirmationType = confirmationType,
                Payload = JsonConvert.SerializeObject(payment),
                CreatedAt = transactionEvent.Timestamp,
                NextAttemptAt = transactionEvent.Timestamp
            });
        }

        private static string NormalizeLocale(string locale) =>
            locale?.StartsWith(
                "de",
                StringComparison.OrdinalIgnoreCase) == true
                ? "de"
                : "en";

        private static bool Matches(
            ServicePerformanceDeclaration evidence,
            string userId,
            int productId,
            string productSlug,
            int count,
            string reference,
            ServicePurchaseRequest request,
            string locale) =>
            evidence.UserId == userId
            && evidence.ProductId == productId
            && evidence.ProductSlug == productSlug
            && evidence.Count == count
            && evidence.PurchaseReference == reference
            && evidence.EarlyPerformanceRequested
                == request.ImmediatePerformanceRequested
            && evidence.WithdrawalConsequenceAcknowledged
                == request.WithdrawalConsequenceAcknowledged
            && evidence.Locale == locale
            && evidence.DeclarationVersion == request.DeclarationVersion
            && evidence.DeclarationText == request.DeclarationText
            && evidence.DeclarationSha256 == request.DeclarationSha256
            && evidence.AgreementId == request.AgreementId
            && evidence.AgreementHash == request.AgreementHash
            && evidence.WithdrawalVersion == request.WithdrawalVersion
            && evidence.WithdrawalSha256 == request.WithdrawalSha256
            && evidence.TaxCountry == request.TaxCountry
            && ConsumerRightsRegime(evidence.TaxCountry)
                == request.ConsumerRightsRegime
            && evidence.VatRateBasisPoints == request.VatRateBasisPoints
            && evidence.GrossEurCents == request.GrossEurCents
            && evidence.VatEurCents == request.VatEurCents
            && evidence.OrderDetailsJson == request.OrderDetailsJson;

        private static string ConsumerRightsRegime(string country) =>
            country switch
            {
                "GB" => "UK",
                "US" => "US",
                _ when EuCountries.Contains(country) => "EU",
                _ => null
            };

        private async Task<ServicePurchaseQuote> Quote(
            User user,
            decimal coinAmount)
        {
            var country = user.Country?.Trim().ToUpperInvariant();
            if (country == "GB")
            {
                var postalCode = await db.PaymentRecords.AsNoTracking()
                    .Where(record => record.ExternalUserId == user.ExternalId
                        && record.Country == "GB")
                    .OrderByDescending(record => record.PaidAt)
                    .Select(record => record.ZipCode)
                    .FirstOrDefaultAsync();
                if (string.IsNullOrWhiteSpace(postalCode)
                    || postalCode.Trim().StartsWith(
                        "BT", StringComparison.OrdinalIgnoreCase))
                    throw new ApiException(
                        "expert_config_tax_quote_unavailable");
            }
            var consumerRightsRegime = country switch
            {
                "GB" => "UK",
                "US" => "US",
                _ when EuCountries.Contains(country) => "EU",
                _ => null
            };
            var valuationCoins = configuration?.GetValue<decimal>(
                "CONVERSION_RATE:Amount") ?? 0;
            var valuationEur = configuration?.GetValue<decimal>(
                "CONVERSION_RATE:Eur") ?? 0;
            var vatRate = country?.Length == 2
                ? configuration?.GetValue<int?>(
                    $"EXPERT_CONFIG:VAT_RATE_BASIS_POINTS:{country}")
                : null;
            if (consumerRightsRegime == null
                || valuationCoins <= 0 || valuationEur <= 0 || !vatRate.HasValue
                || vatRate is < 0 or > 10_000)
                throw new ApiException(
                    "expert_config_tax_quote_unavailable");

            var gross = (long)Math.Round(
                coinAmount * valuationEur * 100m / valuationCoins,
                MidpointRounding.AwayFromZero);
            var net = (long)Math.Round(
                gross * 10_000m / (10_000 + vatRate.Value),
                MidpointRounding.AwayFromZero);
            return new()
            {
                CoinAmount = coinAmount,
                TaxCountry = country,
                ConsumerRightsRegime = consumerRightsRegime,
                VatRateBasisPoints = vatRate.Value,
                GrossEurCents = gross,
                VatEurCents = gross - net,
            };
        }

        private static void ValidateOrderEvidence(
            ServicePurchaseRequest request,
            ServicePurchaseQuote quote)
        {
            if (!string.Equals(request.AgreementId, "expertMarketplace",
                    StringComparison.Ordinal)
                || !string.Equals(request.AgreementHash,
                    ExpertMarketplaceAgreementHash, StringComparison.Ordinal)
                || request.TaxCountry != quote.TaxCountry
                || request.ConsumerRightsRegime
                    != quote.ConsumerRightsRegime
                || request.VatRateBasisPoints != quote.VatRateBasisPoints
                || request.GrossEurCents != quote.GrossEurCents
                || request.VatEurCents != quote.VatEurCents
                || string.IsNullOrWhiteSpace(request.OrderDetailsJson)
                || request.OrderDetailsJson.Length > 524_288)
                throw new ApiException("invalid_expert_config_order_evidence");
            try
            {
                var details = JObject.Parse(request.OrderDetailsJson);
                if (!string.Equals(
                        (string)details["acceptedAgreement"]?["hash"],
                        ExpertMarketplaceAgreementHash,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        (string)details["creatorAgreementHash"],
                        CreatorMarketplaceAgreementHash,
                        StringComparison.Ordinal))
                    throw new JsonException();
            }
            catch (JsonException)
            {
                throw new ApiException("invalid_expert_config_order_evidence");
            }
        }

        private void RequireExpertConfigRollout(string userId)
        {
            if (!string.Equals(userId, "7", StringComparison.Ordinal)
                && timeProvider.GetUtcNow() < ExpertConfigRolloutAt)
                throw new ApiException("expert_config_not_available");
        }

        private static void ValidateDeclaration(ServicePurchaseRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.DeclarationVersion)
                || string.IsNullOrWhiteSpace(request.DeclarationText)
                || !IsSha256(request.DeclarationSha256)
                || !Sha256(request.DeclarationText).Equals(
                    request.DeclarationSha256,
                    StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(request.AgreementId)
                || !IsSha256(request.AgreementHash)
                || string.IsNullOrWhiteSpace(request.WithdrawalVersion)
                || !IsSha256(request.WithdrawalSha256))
                throw new ApiException(
                    "invalid_service_performance_declaration");
        }

        private static bool IsSha256(string value) =>
            value?.Length == 64 && value.All(Uri.IsHexDigit);

        private static string Sha256(string value) =>
            Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

        private static bool IsPremium(string productSlug) =>
            productSlug.StartsWith(
                "premium",
                StringComparison.OrdinalIgnoreCase)
            || productSlug.StartsWith(
                "starter_premium",
                StringComparison.OrdinalIgnoreCase);

        private static readonly HashSet<string> EuCountries =
        [
            "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR",
            "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL",
            "PL", "PT", "RO", "SK", "SI", "ES", "SE"
        ];

        public async Task<RuleResult> GetAdjustedProduct(string productSlug, string userId)
        {
            var product = await GetProduct(productSlug);
            var user = await userService.GetOrCreate(userId, false);
            if (user == null)
                return new();
            return await ruleEngine.GetAdjusted(product, user);
        }

        private async Task<TransactionEvent> ExecuteServicePurchase(
            string productSlug,
            string userId,
            int count,
            string reference,
            Product dbProduct,
            IDbContextTransaction transaction,
            User user,
            Product adjustedProduct,
            bool commitTransaction,
            DateTime? evaluationAtUtc = null,
            bool publishEvent = true,
            long[] slotIds = null)
        {
            var existingOwnerShip = user.Owns?.Where(p => p.Product == dbProduct) ?? new List<OwnerShip>();
            if (existingOwnerShip.Where(p => p.Expires > DateTime.UtcNow + TimeSpan.FromDays(3000)).Any())
            {
                throw new ApiException("already owned for too long");
            }
            if (await db.FiniteTransactions.AnyAsync(item =>
                    item.Product == dbProduct
                    && item.User == user
                    && item.Reference == reference))
                throw new DupplicateTransactionException();
            var price = adjustedProduct.Cost * count;
            if (user.AvailableBalance < price && adjustedProduct.Slug != "revert")
            {
                logger.LogError($"User {user.ExternalId} doesn't have the required {price} amount to purchase {productSlug} (only {user.AvailableBalance} available)");
                throw new ApiException("insuficcient balance");
            }
            List<Product> allProductsToExtend = [];
            if (dbProduct.SlotCount == 0)
            {
                allProductsToExtend = await GetProducts(productSlug, db.Products);
                allProductsToExtend.AddRange(await GetProducts(productSlug, db.TopUpProducts));
            }

            var transactionEvent = await CreateTransaction(dbProduct, user, price * -1, reference, adjustedProduct.OwnershipSeconds);
            if (adjustedProduct.Slug == "revert")
                transactionEvent.RevertedProductSlug = productSlug;
            var time = TimeSpan.FromSeconds(adjustedProduct.OwnershipSeconds * count);
            if (dbProduct.SlotCount > 0)
                await new TierSlotService(db).ApplyPurchase(user, dbProduct, transactionEvent.Id,
                    count, adjustedProduct.OwnershipSeconds, slotIds);
            foreach (var item in allProductsToExtend)
            {
                var existingExpiry = await userService.GetLongest(userId, new() { item.Slug });
                Console.WriteLine(item.Slug + " exires at " + existingExpiry);
                var newExpiry = GetNewExpiry(
                    existingExpiry,
                    time,
                    evaluationAtUtc);
                logger.LogInformation($"User {user.ExternalId} has {existingExpiry} for {item.Slug} and will be extended to {newExpiry} by {time}");
                existingOwnerShip = user.Owns?.Where(p => p.Product?.Id == item.Id);
                if (existingOwnerShip.Any())
                {
                    existingOwnerShip.First().Expires = newExpiry;
                }
                else
                {
                    user.Owns.Add(new OwnerShip() { Expires = newExpiry, Product = item, User = user });
                }
            }

            db.Update(user);
            await db.SaveChangesAsync();
            // commit is handled by the transaction wrapper (WithTransactionAsync) when this method owns the transaction
            if (publishEvent)
                await transactionEventProducer.ProduceEvent(transactionEvent);
            return transactionEvent;
        }

        private static async Task<List<Product>> GetProducts(string productSlug, IQueryable<Product> productA)
        {
            return await productA.Where(p => p.Slug == productSlug).SelectMany(p => p.Groups, (p, g) => g.Products.Where(p => p.Slug == g.Slug).First()).ToListAsync();
        }

        internal async Task<TransactionEvent> RevertPurchase(string userId, long transactionId, bool adjustTime = true)
        {
            var transaction = await db.FiniteTransactions
                .Where(t => t.User.ExternalId == userId && t.Id == transactionId)
                .Include(t => t.Product)
                .FirstOrDefaultAsync();
            if (transaction == null)
                throw new ApiException("Transaction not found");

            var dbProduct = await GetProduct("revert");
            var reference = $"revert transaction {transactionId}";
            var result = await WithTransactionAsync(async (tx, owns) =>
            {
                var existing = await db.FiniteTransactions.AsNoTracking()
                    .Where(item => item.User.ExternalId == userId
                        && item.Product == dbProduct
                        && item.Reference == reference)
                    .Select(item => new TransactionEvent
                    {
                        Amount = Decimal.ToDouble(item.Amount),
                        Id = item.Id,
                        ProductId = item.ProductId,
                        ProductSlug = "revert",
                        RevertedProductSlug = transaction.Product.Slug,
                        Reference = item.Reference,
                        UserId = userId,
                        Timestamp = item.Timestamp
                    })
                    .SingleOrDefaultAsync();
                if (existing != null)
                    return existing;
                var user = await userService.GetOrCreate(userId);
                if (await new TierSlotService(db).Revert(transactionId))
                {
                    var refund = await CreateTransaction(dbProduct, user, -transaction.Amount, reference);
                    refund.RevertedProductSlug = transaction.Product.Slug;
                    return refund;
                }
                var adjustedProduct = (await ruleEngine.GetAdjusted(dbProduct, user)).ModifiedProduct;
                var count = GetRevertPurchaseCount(transaction.Amount, transaction.Product.Cost);
                adjustedProduct.Cost = transaction.Amount / count;
                adjustedProduct.OwnershipSeconds = -transaction.Product.OwnershipSeconds;
                if (!adjustTime)
                    adjustedProduct.OwnershipSeconds = 0;
                adjustedProduct.Slug = "revert";
                return await ExecuteServicePurchase(
                    transaction.Product.Slug, userId, count, reference,
                    dbProduct, tx, user, adjustedProduct, owns,
                    publishEvent: false);
            });
            await transactionEventProducer.ProduceEvent(result);
            return result;
        }

        internal static int GetRevertPurchaseCount(decimal transactionAmount, decimal productCost)
        {
            if (productCost == 0)
                return 1;

            var roundedCount = decimal.Round(
                decimal.Abs(transactionAmount / productCost),
                0,
                MidpointRounding.AwayFromZero);

            if (roundedCount < 1)
                return 1;
            if (roundedCount > int.MaxValue)
                throw new ApiException("Transaction item count is too large to revert");

            return decimal.ToInt32(roundedCount);
        }

        public static DateTime GetNewExpiry(
            DateTime currentTime,
            TimeSpan time,
            DateTime? now = null)
        {
            var effectiveNow = now ?? DateTime.UtcNow;
            if (currentTime < effectiveNow)
                return effectiveNow + time;
            else
                return currentTime += time;
        }

        internal async Task<TransactionEvent> Transfer(string userId, string targetUserId, decimal changeamount, string reference)
        {
            if (changeamount < 1)
                throw new ApiException("The minimum transaction amount is 1");

            return await WithTransactionAsync(async (tx, owns) =>
            {
            var product = db.Products.Where(p => p.Slug == "transfer").FirstOrDefault();
            var initiatingUser = await userService.GetAndInclude(userId, u => u);
            var minTime = DateTime.UtcNow - TimeSpan.FromDays(transferSettings.PeriodDays);
            var totalTransactions = await db.FiniteTransactions.Where(t => t.User == initiatingUser).CountAsync();
            var transactionsSent = await db.FiniteTransactions.Where(t => t.User == initiatingUser && t.Product == product && t.Timestamp > minTime && t.Amount < 0).ToListAsync();
            var limit = transferSettings.Limit;
            if (totalTransactions >= transferSettings.Limit * 5)
            {
                limit = transferSettings.Limit * 2;
            }
            if (transactionsSent.Count() >= limit)
            {
                var nextAvailableIn = (int)(transactionsSent.OrderBy(t => t.Timestamp).First().Timestamp + TimeSpan.FromDays(transferSettings.PeriodDays) - DateTime.UtcNow).TotalHours + 1;
                throw new ApiException($"You reached the maximium of {limit} transactions per {transferSettings.PeriodDays} days. Next available in {nextAvailableIn} hours");
            }
            var targetUser = await userService.GetOrCreate(targetUserId);
            await AssertNotToManyTransfersReceived(product, minTime, targetUser);
            var senderDeduct = -(changeamount);
            if (db.FiniteTransactions.Where(t =>
                 t.Amount == senderDeduct && t.Product == product && t.Reference == reference && t.User == initiatingUser)
                .Any())
                throw new DupplicateTransactionException();

            var transactionEvent = await CreateTransaction(product, initiatingUser, senderDeduct, reference);
            var receiveTransaction = await CreateTransaction(product, targetUser, changeamount, reference);

            await transactionEventProducer.ProduceEvent(transactionEvent);
            await transactionEventProducer.ProduceEvent(receiveTransaction);
            logger.LogInformation("After transaction user {sending} now has {newBalance} and user {receiving} has {newBalanceReceiving}", initiatingUser.ExternalId, initiatingUser.Balance, targetUser.ExternalId, targetUser.Balance);
            return transactionEvent;
            });
        }

        private async Task AssertNotToManyTransfersReceived(PurchaseableProduct product, DateTime minTime, User targetUser)
        {
            var received = await db.FiniteTransactions.Where(t => t.User == targetUser && t.Product == product && t.Timestamp > minTime && t.Amount > 0).ToListAsync();
            if (received.Count(r => r.Amount > 100) <= transferSettings.Limit / 2 && received.Where(r => r.Amount < 100).Sum(r => r.Amount) <= (transferSettings.Limit * 100)
                || received.Count <= transferSettings.Limit / 2)
                return;
            var ends = received.Select(r => r.Timestamp).OrderBy(d => d).First() + TimeSpan.FromDays(transferSettings.PeriodDays) - DateTime.UtcNow;
            throw new ApiException($"The target user has received too many transfers recently. Can receive again in {(int)ends.TotalHours + 1} hours");
        }

        /// <summary>
        /// Thrown if the transaction was already executed
        /// </summary>
        public class DupplicateTransactionException : ApiException
        {
            /// <summary>
            /// Creates a new instance <see cref="DupplicateTransactionException"/>
            /// </summary>
            /// <returns></returns>
            public DupplicateTransactionException() : base("This transaction already happened (same reference found)")
            {
            }
        }
        /// <summary>
        /// Thrown if an user doesn't have enough funds
        /// </summary>
        public class InsufficientFundsException : ApiException
        {
            /// <summary>
            /// Creates a new instance <see cref="InsufficientFundsException"/>
            /// </summary>
            /// <returns></returns>
            public InsufficientFundsException(decimal required, decimal available) : base($"You don't have enough funds to make this transaction. Required {required} Available: {available}")
            {
            }
        }
    }
}
