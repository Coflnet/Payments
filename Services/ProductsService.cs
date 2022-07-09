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
        InvalidateProduct(oldProduct);

        db.Add(product);
        await groupService.AddProductToGroup(product, product.Slug);
        await db.SaveChangesAsync();
        return await db.TopUpProducts.Where(p => p.Slug == product.Slug).FirstOrDefaultAsync();
    }

    private void InvalidateProduct(Product oldProduct)
    {
        if (oldProduct == null)
            return;
        // change the old slug
        var newSlug = oldProduct.Slug.Truncate(18) + Convert.ToBase64String(BitConverter.GetBytes(DateTime.UtcNow.Ticks % 100000).Reverse().ToArray());
        oldProduct.Slug = newSlug.Truncate(20);
        oldProduct.Type |= Product.ProductType.DISABLED;
        db.Update(oldProduct);

    }
}

