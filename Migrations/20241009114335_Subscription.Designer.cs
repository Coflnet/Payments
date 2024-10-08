﻿// <auto-generated />
using System;
using System.Net;
using Coflnet.Payments.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Payments.Migrations
{
    [DbContext(typeof(PaymentContext))]
    [Migration("20241009114335_Subscription")]
    partial class Subscription
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "8.0.3")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("Coflnet.Payments.Models.FiniteTransaction", b =>
                {
                    b.Property<long>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("bigint");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<long>("Id"));

                    b.Property<decimal>("Amount")
                        .HasColumnType("numeric");

                    b.Property<int>("ProductId")
                        .HasColumnType("integer");

                    b.Property<string>("Reference")
                        .HasMaxLength(80)
                        .HasColumnType("character varying(80)");

                    b.Property<DateTime>("Timestamp")
                        .HasColumnType("timestamp with time zone");

                    b.Property<int?>("UserId")
                        .HasColumnType("integer");

                    b.HasKey("Id");

                    b.HasIndex("ProductId");

                    b.HasIndex("Reference");

                    b.HasIndex("UserId");

                    b.ToTable("FiniteTransactions");
                });

            modelBuilder.Entity("Coflnet.Payments.Models.Group", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<string>("Slug")
                        .HasMaxLength(32)
                        .HasColumnType("character varying(32)");

                    b.HasKey("Id");

                    b.HasIndex("Slug")
                        .IsUnique();

                    b.ToTable("Groups");
                });

            modelBuilder.Entity("Coflnet.Payments.Models.License", b =>
                {
                    b.Property<long>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("bigint");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<long>("Id"));

                    b.Property<DateTime>("Expires")
                        .HasColumnType("timestamp with time zone");

                    b.Property<int?>("ProductId")
                        .HasColumnType("integer");

                    b.Property<string>("TargetId")
                        .HasColumnType("text");

                    b.Property<int?>("UserId")
                        .HasColumnType("integer");

                    b.Property<int?>("groupId")
                        .HasColumnType("integer");

                    b.HasKey("Id");

                    b.HasIndex("ProductId");

                    b.HasIndex("groupId");

                    b.HasIndex("TargetId", "Expires");

                    b.HasIndex("UserId", "TargetId", "Expires");

                    b.HasIndex("UserId", "TargetId", "ProductId")
                        .IsUnique();

                    b.ToTable("Licenses", (string)null);
                });

            modelBuilder.Entity("Coflnet.Payments.Models.OwnerShip", b =>
                {
                    b.Property<long>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("bigint");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<long>("Id"));

                    b.Property<DateTime>("Expires")
                        .HasColumnType("timestamp with time zone");

                    b.Property<int?>("ProductId")
                        .HasColumnType("integer");

                    b.Property<int?>("UserId")
                        .HasColumnType("integer");

                    b.HasKey("Id");

                    b.HasIndex("Expires");

                    b.HasIndex("ProductId");

                    b.HasIndex("UserId");

                    b.ToTable("OwnerShip", (string)null);
                });

            modelBuilder.Entity("Coflnet.Payments.Models.PaymentRequest", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<decimal>("Amount")
                        .HasColumnType("numeric");

                    b.Property<IPAddress>("CreateOnIp")
                        .HasColumnType("inet");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("DeviceFingerprint")
                        .HasMaxLength(32)
                        .HasColumnType("character varying(32)");

                    b.Property<DateTime?>("ExpiresAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("Locale")
                        .HasMaxLength(5)
                        .HasColumnType("character varying(5)");

                    b.Property<int?>("ProductIdId")
                        .HasColumnType("integer");

                    b.Property<string>("Provider")
                        .HasMaxLength(32)
                        .HasColumnType("character varying(32)");

                    b.Property<string>("SessionId")
                        .HasMaxLength(75)
                        .HasColumnType("character varying(75)");

                    b.Property<int>("State")
                        .HasColumnType("integer");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<int?>("UserId")
                        .HasColumnType("integer");

                    b.HasKey("Id");

                    b.HasIndex("CreateOnIp");

                    b.HasIndex("DeviceFingerprint");

                    b.HasIndex("ProductIdId");

                    b.HasIndex("UserId");

                    b.HasIndex("CreatedAt", "SessionId");

                    b.ToTable("PaymentRequests");
                });

            modelBuilder.Entity("Coflnet.Payments.Models.PlanedTransaction", b =>
                {
                    b.Property<long>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("bigint");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<long>("Id"));

                    b.Property<decimal>("Amount")
                        .HasColumnType("numeric");

                    b.Property<int>("ProductId")
                        .HasColumnType("integer");

                    b.Property<string>("Reference")
                        .HasMaxLength(80)
                        .HasColumnType("character varying(80)");

                    b.Property<DateTime>("Timestamp")
                        .HasColumnType("timestamp with time zone");

                    b.Property<int?>("UserId")
                        .HasColumnType("integer");

                    b.HasKey("Id");

                    b.HasIndex("ProductId");

                    b.HasIndex("UserId");

                    b.ToTable("PlanedTransactions");
                });

            modelBuilder.Entity("Coflnet.Payments.Models.Product", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<decimal>("Cost")
                        .HasColumnType("numeric");

                    b.Property<string>("Description")
                        .HasColumnType("text");

                    b.Property<string>("Discriminator")
                        .IsRequired()
                        .HasMaxLength(21)
                        .HasColumnType("character varying(21)");

                    b.Property<long>("OwnershipSeconds")
                        .HasColumnType("bigint");

                    b.Property<string>("Slug")
                        .HasMaxLength(32)
                        .HasColumnType("character varying(32)");

                    b.Property<string>("Title")
                        .HasMaxLength(80)
                        .HasColumnType("character varying(80)");

                    b.Property<int>("Type")
                        .HasColumnType("integer");

                    b.HasKey("Id");

                    b.ToTable("Product");

                    b.HasDiscriminator<string>("Discriminator").HasValue("Product");

                    b.UseTphMappingStrategy();
                });

            modelBuilder.Entity("Coflnet.Payments.Models.Rule", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<decimal>("Amount")
                        .HasColumnType("numeric");

                    b.Property<int>("Flags")
                        .HasColumnType("integer");

                    b.Property<int>("Priority")
                        .HasColumnType("integer");

                    b.Property<int?>("RequiresId")
                        .HasColumnType("integer");

                    b.Property<string>("Slug")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<int>("TargetsId")
                        .HasColumnType("integer");

                    b.HasKey("Id");

                    b.HasIndex("RequiresId");

                    b.HasIndex("Slug")
                        .IsUnique();

                    b.HasIndex("TargetsId");

                    b.ToTable("Rules");
                });

            modelBuilder.Entity("Coflnet.Payments.Models.User", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<decimal>("Balance")
                        .HasColumnType("numeric");

                    b.Property<string>("Country")
                        .HasMaxLength(2)
                        .HasColumnType("character varying(2)");

                    b.Property<string>("ExternalId")
                        .HasMaxLength(32)
                        .HasColumnType("character varying(32)");

                    b.Property<string>("Ip")
                        .HasMaxLength(45)
                        .HasColumnType("character varying(45)");

                    b.Property<string>("Locale")
                        .HasMaxLength(5)
                        .HasColumnType("character varying(5)");

                    b.Property<string>("Zip")
                        .HasMaxLength(10)
                        .HasColumnType("character varying(10)");

                    b.HasKey("Id");

                    b.HasIndex("ExternalId")
                        .IsUnique();

                    b.HasIndex("Ip");

                    b.ToTable("Users");
                });

            modelBuilder.Entity("Coflnet.Payments.Models.UserSubscription", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<DateTime?>("EndsAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("ExternalCustomerId")
                        .HasColumnType("text");

                    b.Property<string>("ExternalId")
                        .HasColumnType("text");

                    b.Property<string>("PaymentAmount")
                        .HasColumnType("text");

                    b.Property<int?>("ProductId")
                        .HasColumnType("integer");

                    b.Property<string>("ProviderSlug")
                        .HasColumnType("text");

                    b.Property<DateTime>("RenewsAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("Status")
                        .HasColumnType("text");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<int?>("UserId")
                        .HasColumnType("integer");

                    b.HasKey("Id");

                    b.HasIndex("ExternalId");

                    b.HasIndex("ProductId");

                    b.HasIndex("UserId");

                    b.ToTable("Subscriptions");
                });

            modelBuilder.Entity("GroupProduct", b =>
                {
                    b.Property<int>("GroupsId")
                        .HasColumnType("integer");

                    b.Property<int>("ProductsId")
                        .HasColumnType("integer");

                    b.HasKey("GroupsId", "ProductsId");

                    b.HasIndex("ProductsId");

                    b.ToTable("GroupProduct");
                });

            modelBuilder.Entity("Coflnet.Payments.Models.PurchaseableProduct", b =>
                {
                    b.HasBaseType("Coflnet.Payments.Models.Product");

                    b.HasIndex("Slug")
                        .IsUnique();

                    b.HasDiscriminator().HasValue("PurchaseableProduct");
                });

            modelBuilder.Entity("Coflnet.Payments.Models.TopUpProduct", b =>
                {
                    b.HasBaseType("Coflnet.Payments.Models.Product");

                    b.Property<string>("CurrencyCode")
                        .HasMaxLength(3)
                        .HasColumnType("character varying(3)");

                    b.Property<decimal>("Price")
                        .HasColumnType("numeric");

                    b.Property<string>("ProviderSlug")
                        .HasMaxLength(16)
                        .HasColumnType("character varying(16)");

                    b.HasIndex("Slug", "ProviderSlug")
                        .IsUnique();

                    b.HasDiscriminator().HasValue("TopUpProduct");
                });

            modelBuilder.Entity("Coflnet.Payments.Models.FiniteTransaction", b =>
                {
                    b.HasOne("Coflnet.Payments.Models.Product", "Product")
                        .WithMany()
                        .HasForeignKey("ProductId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("Coflnet.Payments.Models.User", "User")
                        .WithMany()
                        .HasForeignKey("UserId");

                    b.Navigation("Product");

                    b.Navigation("User");
                });

            modelBuilder.Entity("Coflnet.Payments.Models.License", b =>
                {
                    b.HasOne("Coflnet.Payments.Models.PurchaseableProduct", "Product")
                        .WithMany()
                        .HasForeignKey("ProductId");

                    b.HasOne("Coflnet.Payments.Models.User", "User")
                        .WithMany()
                        .HasForeignKey("UserId");

                    b.HasOne("Coflnet.Payments.Models.Group", "group")
                        .WithMany()
                        .HasForeignKey("groupId");

                    b.Navigation("Product");

                    b.Navigation("User");

                    b.Navigation("group");
                });

            modelBuilder.Entity("Coflnet.Payments.Models.OwnerShip", b =>
                {
                    b.HasOne("Coflnet.Payments.Models.Product", "Product")
                        .WithMany()
                        .HasForeignKey("ProductId");

                    b.HasOne("Coflnet.Payments.Models.User", "User")
                        .WithMany("Owns")
                        .HasForeignKey("UserId");

                    b.Navigation("Product");

                    b.Navigation("User");
                });

            modelBuilder.Entity("Coflnet.Payments.Models.PaymentRequest", b =>
                {
                    b.HasOne("Coflnet.Payments.Models.TopUpProduct", "ProductId")
                        .WithMany()
                        .HasForeignKey("ProductIdId");

                    b.HasOne("Coflnet.Payments.Models.User", "User")
                        .WithMany()
                        .HasForeignKey("UserId");

                    b.Navigation("ProductId");

                    b.Navigation("User");
                });

            modelBuilder.Entity("Coflnet.Payments.Models.PlanedTransaction", b =>
                {
                    b.HasOne("Coflnet.Payments.Models.Product", "Product")
                        .WithMany()
                        .HasForeignKey("ProductId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("Coflnet.Payments.Models.User", "User")
                        .WithMany()
                        .HasForeignKey("UserId");

                    b.Navigation("Product");

                    b.Navigation("User");
                });

            modelBuilder.Entity("Coflnet.Payments.Models.Rule", b =>
                {
                    b.HasOne("Coflnet.Payments.Models.Group", "Requires")
                        .WithMany()
                        .HasForeignKey("RequiresId");

                    b.HasOne("Coflnet.Payments.Models.Group", "Targets")
                        .WithMany()
                        .HasForeignKey("TargetsId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Requires");

                    b.Navigation("Targets");
                });

            modelBuilder.Entity("Coflnet.Payments.Models.UserSubscription", b =>
                {
                    b.HasOne("Coflnet.Payments.Models.Product", "Product")
                        .WithMany()
                        .HasForeignKey("ProductId");

                    b.HasOne("Coflnet.Payments.Models.User", "User")
                        .WithMany()
                        .HasForeignKey("UserId");

                    b.Navigation("Product");

                    b.Navigation("User");
                });

            modelBuilder.Entity("GroupProduct", b =>
                {
                    b.HasOne("Coflnet.Payments.Models.Group", null)
                        .WithMany()
                        .HasForeignKey("GroupsId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("Coflnet.Payments.Models.Product", null)
                        .WithMany()
                        .HasForeignKey("ProductsId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();
                });

            modelBuilder.Entity("Coflnet.Payments.Models.User", b =>
                {
                    b.Navigation("Owns");
                });
#pragma warning restore 612, 618
        }
    }
}
