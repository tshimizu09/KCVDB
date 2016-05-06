﻿using KCVDB.Utils;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Table;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace KCVDB.Services.BlobStorage
{
    public class AzureBlobApiDataWriter : IApiDataWriter
	{
		public CloudBlobContainer BlobContainer { get; }

        public CloudTable TableContainer { get; }

		public AzureBlobApiDataWriter(CloudBlobContainer blobContainer, CloudTable tableContainer)
		{
			if (blobContainer == null) { throw new ArgumentNullException(nameof(blobContainer)); }
            if (tableContainer == null) { throw new ArgumentNullException(nameof(tableContainer)); }
            BlobContainer = blobContainer;
            TableContainer = tableContainer;
		}

        /// <summary>
        /// 単一書き込み
        /// </summary>
        /// <param name="agentId">agentId</param>
        /// <param name="sessionId">sessionId</param>
        /// <param name="apiData">単一送信データ</param>
        /// <returns></returns>
		public Task WriteAsync(string agentId, string sessionId, ApiData apiData)
		{
			return WriteAsync(agentId, sessionId, new ApiData[] { apiData });
		}

        /// <summary>
        /// 複数書き込み
        /// </summary>
        /// <param name="agentId">agentId</param>
        /// <param name="sessionId">sessionId</param>
        /// <param name="apiData">複数送信データ</param>
        /// <returns></returns>
		public async Task WriteAsync(string agentId, string sessionId, IEnumerable<ApiData> apiData)
		{
			if (apiData == null)
            {
                throw new ArgumentNullException(nameof(apiData));
            }
			if (agentId == null)
            {
                throw new ArgumentNullException(nameof(agentId));
            }
			if (sessionId == null)
            {
                throw new ArgumentNullException(nameof(sessionId));
            }

			// コンテナなかったら作る
			await BlobContainer.CreateIfNotExistsAsync();

            // テーブルがなかったら作る
            await TableContainer.CreateIfNotExistsAsync();

            //// 現在日付
            DateTime now = DateTime.UtcNow.Add(Constants.BlobStorage.OffsetGMT);
            var date = now.Date.Add(Constants.BlobStorage.OffsetTime);
            // TableStorageから取得
            var retrieveOperation = TableOperation.Retrieve<SessionEntity>("sessionId", sessionId);
            var retrievedResult = TableContainer.Execute(retrieveOperation);
            var sessionEntity = retrievedResult.Result as SessionEntity;

            // 要分割
            if (sessionEntity?.BlobCreated != null && sessionEntity.BlobCreated < date)
            {
                // api_portが含まれる要素のindexを取得
                var firstPortIndex = FindFirstApiIndexOf(apiData, Constants.BlobStorage.ApiPortPath);

                var beforePort = firstPortIndex < 0 ? apiData : apiData.Take(firstPortIndex + 1);
                var afterPort = firstPortIndex < 0 ? Enumerable.Empty<ApiData>() : apiData.Skip(firstPortIndex);

                if (beforePort.Any())
                {
                    var appendBlob = BlobContainer.GetAppendBlobReference(sessionEntity.BlobName);
                    await WriteBlob(appendBlob, beforePort, agentId, sessionId);
                }

                if (afterPort.Any())
                {
                    var blobName = GenerateAppendBlobName(now, sessionId);

                    sessionEntity.BlobCreated = now;
                    sessionEntity.BlobName = blobName;

                    var operation = TableOperation.InsertOrReplace(sessionEntity);
                    await TableContainer.ExecuteAsync(operation);

                    var appendBlob = BlobContainer.GetAppendBlobReference(sessionEntity.BlobName);
                    await WriteBlob(appendBlob, afterPort, agentId, sessionId);
                }
            }
            // 分割いらにょ
            else
            {
                // 今日初めての書き込みならセッション情報を作成
                if (sessionEntity == null)
                {
                    var blobName = GenerateAppendBlobName(now, sessionId);
                    sessionEntity = new SessionEntity(sessionId)
                    {
                        BlobName = blobName,
                        BlobCreated = now,
                    };
                    // テーブル更新
                    var operation = TableOperation.InsertOrReplace(sessionEntity);
                    await TableContainer.ExecuteAsync(operation);
                }

                var appendBlob = BlobContainer.GetAppendBlobReference(GenerateAppendBlobName(now, sessionId));
                await WriteBlob(appendBlob, apiData, agentId, sessionId);
            }
		}
        
        /// <summary>
        /// Blob書き込み
        /// </summary>
        /// <param name="appendBlob">Blobコンテナー</param>
        /// <param name="apiDatas">データリスト</param>
        /// <param name="agentId">agentId</param>
        /// <param name="sessionId">sessionId</param>
        /// <returns></returns>
        private async Task WriteBlob(CloudAppendBlob appendBlob, IEnumerable<ApiData> apiDatas, string agentId, string sessionId)
        {
            // データを文字列に変換
            var serializedTexts = apiDatas.Select(x => SerializeApiData(agentId, sessionId, x) + Constants.BlobStorage.ApiRawFileNewLine);
            var textToWrite = string.Concat(serializedTexts);

            // Blobが生成されていなければ作成
            if (!await appendBlob.ExistsAsync())
            {
                await appendBlob.CreateOrReplaceAsync();
            }

            // ....〆(･ω･｀ )ｶｷｶｷ
            await appendBlob.AppendTextAsync(textToWrite);
        }

        /// <summary>
        /// AppendBlob取得
        /// </summary>
        /// <param name="date">AppendBlob作成日付</param>
        /// <param name="sessionId">sessionId</param>
        /// <returns></returns>
        private string GenerateAppendBlobName(DateTime date, string sessionId)
        {
            return string.Format(
                        Constants.BlobStorage.BlobFileNameFormat,
                        date.ToString(Constants.BlobStorage.BlobFileNameDateTimeToStringFormat),
                        sessionId.ToLower());
        }

		string SerializeApiData(string agentId, string sessionId, ApiData apiData)
		{
			var columns = new string[]{
				agentId,
				sessionId,
				apiData.RequestUri,
				apiData.StatusCode?.ToString() ?? "",
				apiData.HttpDate,
				apiData.LocalTime,
				apiData.RequestBody,
				apiData.ResponseBody
			};

			// TSVに変換
			return string.Join(
				"\t",
				columns.Select(x => x?.RemoveNewLiens() ?? ""));
		}

        /// <summary>
        /// 受信したデータからapi_portを含んだ要素を検索
        /// </summary>
        /// <param name="apiDatas">受信したデータ</param>
        /// <returns>一致した要素が見つかったindex</returns>
        int FindFirstApiIndexOf(IEnumerable<ApiData> apiDatas, string apiUrl)
        {
            return (apiDatas.Select((x, i) => new { Data = x, Index = i })
                            .FirstOrDefault(x => x.Data.RequestUri.Contains(apiUrl))
                            ?.Index ?? -1);
        }

        #region テストメソッド
        public List<string> ReadTableStorage()
        {
            // Construct the query operation for all customer entities where PartitionKey="Smith".
            TableQuery<SessionEntity> query = new TableQuery<SessionEntity>().Where(TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, "sessionId"));

            List<string> strList = new List<string>();

            var exe = TableContainer.ExecuteQuery(query);
            // Print the fields for each customer.
            foreach (SessionEntity entity in exe)
            {
                string str = entity.PartitionKey + entity.RowKey + entity.BlobName + entity.BlobCreated.ToShortDateString();

                strList.Add(str);
            }

            return strList;
        }

        public string ReadTableStorageOnly()
        {
            string str = "";
            var session = new SessionEntity();

            var retrieveOperation = TableOperation.Retrieve<SessionEntity>("sessionId", "送信セッション");

            var exe = TableContainer.Execute(retrieveOperation);
            // Print the fields for each customer.

            if (exe.Result != null)
            {
                session = exe.Result as SessionEntity;
                str = session.BlobName;
            }

            return str;
        }

        public void WriteTableStorage()
        {
            TableContainer.CreateIfNotExists();
            // Construct the query operation for all customer entities where PartitionKey="Smith".
            TableQuery<SessionEntity> query = new TableQuery<SessionEntity>();

            DateTime now = DateTime.Now;
            var sessionId = Guid.NewGuid().ToString();

            // Create a new customer entity.
            SessionEntity session = new SessionEntity("セッションID固定");
            session.BlobName = GenerateAppendBlobName(now, sessionId);
            session.BlobCreated = now;

            // Create the TableOperation object that inserts the customer entity.
            TableOperation insertOperation = TableOperation.Insert(session);

            // Execute the insert operation.
            TableContainer.Execute(insertOperation);

        }

        public void InsertorReplaceTableStorage()
        {
            TableContainer.CreateIfNotExists();
            // Construct the query operation for all customer entities where PartitionKey="Smith".
            TableQuery<SessionEntity> query = new TableQuery<SessionEntity>();

            DateTime now = DateTime.Now;
            var sessionId = "sessionsession";

            // Create a new customer entity.
            SessionEntity session = new SessionEntity("セッションID固定");
            session.BlobName = GenerateAppendBlobName(now, sessionId);
            session.BlobCreated = now;

            // Create the TableOperation object that inserts the customer entity.
            TableOperation insertOperation = TableOperation.InsertOrReplace(session);

            // Execute the insert operation.
            TableContainer.Execute(insertOperation);

        }
        #endregion
    }
}