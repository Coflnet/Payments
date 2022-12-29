using Coflnet.Payments.Models;
using System.Linq;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System;

namespace Coflnet.Payments.Services;

/// <summary>
/// Manages products
/// </summary>
public class ProductService
{
    private ILogger<ProductService> logger;
    private PaymentContext db;
    private readonly GroupService groupService;

    /// <summary>
    /// Instantiates a new instance of the <see cref="ProductService"/>
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="context"></param>
    /// <param name="groupService"></param>
    public ProductService(
        ILogger<ProductService> logger,
        PaymentContext context,
        GroupService groupService)
    {
        this.logger = logger;
        db = context;
        this.groupService = groupService;
    }

    /// <summary>
    /// Get a product by slug
    /// </summary>
    /// <param name="slug"></param>
    /// <param name="require"></param>
    /// <returns></returns>
    public async Task<PurchaseableProduct> GetProduct(string slug, bool require = true)
    {
        var product = await db.Products.Where(p => p.Slug == slug).FirstOrDefaultAsync();
        if (product == null && require)
            throw new ApiException($"product with slug '{slug}' not found");
        return product;
    }

    /// <summary>
    /// GetTopupOption
    /// </summary>
    /// <param name="slug"></param>
    /// <param name="require"></param>
    /// <returns></returns>
    public async Task<TopUpProduct> GetTopupProduct(string slug, bool require = true)
    {
        var product = await db.TopUpProducts.Where(p => p.Slug == slug).FirstOrDefaultAsync();
        if (product == null && require)
            throw new ApiException($"product with slug '{slug}' not found");
        return product;
    }


    /// <summary>
    /// GetTopupOptions
    /// </summary>
    /// <returns></returns>
    public async Task<List<TopUpProduct>> GetTopupProducts()
    {
        return await db.TopUpProducts.ToListAsync();
    }

    public async Task<PurchaseableProduct> UpdateOrAddProduct(PurchaseableProduct product)
    {
        var oldProduct = await GetProduct(product.Slug, false);
        InvalidateProduct(oldProduct);

        db.Add(product);
        await groupService.AddProductToGroup(product, product.Slug);
        await db.SaveChangesAsync();
        return await db.Products.Where(p => p.Slug == product.Slug).FirstOrDefaultAsync();
    }

    public async Task<TopUpProduct> UpdateTopUpProduct(TopUpProduct product)
    {
        var oldProduct = await GetTopupProduct(product.Slug, false);
        if (product.Equals(oldProduct))
            return oldProduct;
        InvalidateProduct(oldProduct);

        db.Add(product);
        await groupService.AddProductToGroup(product, product.Slug);
        await db.SaveChangesAsync();
        return await db.TopUpProducts.Where(p => p.Slug == product.Slug).FirstOrDefaultAsync();
    }

    public async Task ApplyProductList(List<PurchaseableProduct> products)
    {
        await BatchApply(products, db.Products);
    }
    public async Task ApplyTopupList(List<TopUpProduct> products)
    {
        await BatchApply(products, db.TopUpProducts);
    }

    private async Task BatchApply<T>(List<T> products, DbSet<T> table) where T : Product
    {
        var slugs = products.Select(p => p.Slug).ToHashSet();
        var existingProducts = await table.Where(p => slugs.Contains(p.Slug)).ToListAsync();
        var toDeactivate = await table.Where(p => !slugs.Contains(p.Slug) && !p.Type.HasFlag(Product.ProductType.DISABLED)).ToListAsync();
        foreach (var product in products)
        {
            var existing = existingProducts.FirstOrDefault(p => p.Slug == product.Slug);
            if (product.Cost == existing?.Cost
                && product.Title == existing?.Title
                && (!(product is TopUpProduct topup) || topup.Price == (existing as TopUpProduct)?.Price)
                && product.Description == existing?.Description
                && product.Type == existing?.Type
                && product.OwnershipSeconds == existing?.OwnershipSeconds)
                continue; // nothing changed
            InvalidateProduct(existing);
            db.Add(product);
            logger.LogInformation($"Adding product {product.Slug}");
            await groupService.AddProductToGroup(product, product.Slug);
        }
        foreach (var product in toDeactivate)
        {
            product.Type |= Product.ProductType.DISABLED;
        }
        await db.SaveChangesAsync();
    }

    private void InvalidateProduct(Product oldProduct)
    {
        if (oldProduct == null)
            return;
        // change the old slug
        var newSlug = oldProduct.Slug.Truncate(18) + Convert.ToBase64String(BitConverter.GetBytes(DateTime.UtcNow.Ticks % 10_000_000).Reverse().ToArray());
        oldProduct.Slug = newSlug.Truncate(32);
        oldProduct.Type |= Product.ProductType.DISABLED;
        db.Update(oldProduct);
        logger.LogInformation($"Disabling old product {oldProduct.Slug}");
    }
}

