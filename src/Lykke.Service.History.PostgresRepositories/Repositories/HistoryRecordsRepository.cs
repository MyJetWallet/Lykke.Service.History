﻿using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using Dapper;
using Lykke.Service.History.Core.Domain.Enums;
using Lykke.Service.History.Core.Domain.History;
using Lykke.Service.History.PostgresRepositories.Entities;
using Lykke.Service.History.PostgresRepositories.JsonbQuery;
using Lykke.Service.History.PostgresRepositories.Mappings;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PostgreSQLCopyHelper;

namespace Lykke.Service.History.PostgresRepositories.Repositories
{
    public class HistoryRecordsRepository : IHistoryRecordsRepository
    {
        private const string DuplicateSqlState = "23505";
        private static readonly PostgreSQLCopyHelper<HistoryEntity> BulkMapping;
        private readonly ConnectionFactory _connectionFactory;

        private readonly string _insertQuery = $@"
insert into {Constants.HistoryTableName}(id, wallet_id, asset_id, assetpair_id, volume, type, create_dt, context)
    values (@Id, @WalletId, @AssetId, @AssetPairId, @Volume, @Type, @Timestamp, @Context::jsonb)
ON CONFLICT (id, wallet_id) DO NOTHING;
";

        private readonly string _deleteQuery = $@"
delete from {Constants.HistoryTableName}
where id = @Id and wallet_id = @WalletId
";

        private readonly string _updateBlockchainHashQuery = $@"
update {Constants.HistoryTableName}
set context = jsonb_set(coalesce(context, '{{}}'), '{{{
                nameof(HistoryEntityContext.BlockchainHash)
            }}}', coalesce(to_jsonb(@Hash::text), jsonb 'null'))
where id = @Id
";

        private readonly string _tradesDateRangeQuery = $@"SELECT * FROM {Constants.HistoryTableName}
WHERE type = {(int)HistoryType.Trade} AND create_dt >= '{{0}}' AND create_dt < '{{1}}' ORDER BY create_dt
LIMIT {{2}} OFFSET {{3}}";

        static HistoryRecordsRepository()
        {
            BulkMapping = HistoryEntityBulkMapping.Generate();
            SqlMapper.SetTypeMap(
                typeof(HistoryEntity),
                new CustomPropertyTypeMap(
                    typeof(HistoryEntity),
                    (type, columnName) =>
                        type.GetProperties().FirstOrDefault(prop =>
                            prop.GetCustomAttributes(false)
                                .OfType<ColumnAttribute>()
                                .Any(attr => attr.Name == columnName))));
        }

        public HistoryRecordsRepository(ConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task<BaseHistoryRecord> GetAsync(Guid id, Guid walletId)
        {
            using (var connection = _connectionFactory.CreateDataContext())
            {
                return HistoryTypeMapper.Map(
                    await connection.History.FirstOrDefaultAsync(x => x.Id == id && x.WalletId == walletId));
            }
        }

        public async Task<bool> InsertBulkAsync(IEnumerable<BaseHistoryRecord> records)
        {
            using (var connection = await _connectionFactory.CreateNpgsqlConnection())
            {
                var baseHistoryRecords = records.ToArray();

                try
                {
                    BulkMapping.SaveAll(connection, baseHistoryRecords.Select(HistoryTypeMapper.Map));
                }
                catch (PostgresException e) when (e.SqlState == DuplicateSqlState)
                {
                    // fallback, try to insert one by one
                    foreach (var trade in baseHistoryRecords)
                        await TryInsertAsync(trade);
                }
            }

            return true;
        }

        public async Task<bool> TryInsertAsync(BaseHistoryRecord entity)
        {
            using (var connection = await _connectionFactory.CreateNpgsqlConnection())
            {
                var result = await connection.ExecuteAsync(_insertQuery, HistoryTypeMapper.Map(entity));

                return result > 0;
            }
        }

        public async Task<bool> TryDeleteAsync(Guid operationId, Guid walletId)
        {
            using (var connection = await _connectionFactory.CreateNpgsqlConnection())
            {
                var result = await connection.ExecuteAsync(_deleteQuery, new
                {
                    Id = operationId,
                    WalletId = walletId
                });

                return result > 0;
            }
        }

        public async Task<bool> UpdateBlockchainHashAsync(Guid id, string hash)
        {
            using (var connection = await _connectionFactory.CreateNpgsqlConnection())
            {
                var result = await connection.ExecuteAsync(_updateBlockchainHashQuery, new
                {
                    Id = id,
                    Hash = hash
                });

                return result > 0;
            }
        }

        public async Task<IEnumerable<BaseHistoryRecord>> GetByWalletAsync(
            Guid walletId,
            HistoryType[] type,
            int offset,
            int limit,
            string assetPairId = null,
            string assetId = null,
            DateTime? fromDt = null,
            DateTime? toDt = null)
        {
            using (var context = _connectionFactory.CreateDataContext())
            {
                var query = context.History
                    .Where(x => x.WalletId == walletId && type.Contains(x.Type))
                    .Where(x => string.IsNullOrWhiteSpace(assetPairId) || x.AssetPairId == assetPairId)
                    .Where(x => string.IsNullOrWhiteSpace(assetId) || x.AssetId == assetId ||
                                (x.Type == HistoryType.Trade && x.Context.JsonbPath<string>(nameof(HistoryEntityContext.TradeQuotingAssetId)) == assetId))
                    .Where(x => fromDt == null || (x.Timestamp > fromDt.Value))
                    .Where(x => toDt == null || (x.Timestamp <= toDt.Value))
                    .OrderByDescending(x => x.Timestamp)
                    .Skip(offset)
                    .Take(limit);

                return (await query.ToListAsync()).Select(HistoryTypeMapper.Map);
            }
        }

        public async Task<IEnumerable<Trade>> GetTradesByWalletAsync(
            Guid walletId,
            int offset,
            int limit,
            string assetPairId = null,
            string assetId = null,
            DateTime? fromDt = null,
            DateTime? toDt = null,
            bool? buyTrades = null)
        {
            using (var context = _connectionFactory.CreateDataContext())
            {
                var query = context.History
                    .Where(x => x.WalletId == walletId && x.Type == HistoryType.Trade)
                    .Where(x => buyTrades == null || (buyTrades == true && x.Volume > 0) || (buyTrades == false && x.Volume < 0))
                    .Where(x => string.IsNullOrWhiteSpace(assetPairId) || x.AssetPairId == assetPairId)
                    .Where(x => string.IsNullOrWhiteSpace(assetId) || x.AssetId == assetId || x.Context.JsonbPath<string>(nameof(HistoryEntityContext.TradeQuotingAssetId)) == assetId)
                    .Where(x => fromDt == null || (x.Timestamp > fromDt.Value))
                    .Where(x => toDt == null || (x.Timestamp <= toDt.Value))
                    .OrderByDescending(x => x.Timestamp)
                    .Skip(offset)
                    .Take(limit);

                return Mapper.Map<IEnumerable<Trade>>(await query.ToListAsync());
            }
        }

        public async Task<IEnumerable<Trade>> GetByDatesAsync(
            DateTime fromDt,
            DateTime toDt,
            int offset,
            int limit)
        {
            if (fromDt >= toDt)
                return new Trade[0];

            using (var connection = await _connectionFactory.CreateNpgsqlConnection())
            {
                var query = string.Format(
                    _tradesDateRangeQuery,
                    fromDt.ToString(Constants.DateTimeFormat),
                    toDt.ToString(Constants.DateTimeFormat),
                    limit,
                    offset);
                var items = await connection.QueryAsync<HistoryEntity>(query);

                return items.Select(Mapper.Map<Trade>);
            }
        }
    }
}
