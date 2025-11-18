using MongoDB.Bson;
using System;

using MongoDB.Driver;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace ProcessorApplication.Database;

//todo: move to respective module
public static class MongoDbConfig
{
    /// <summary>
    /// Initializes MongoDB if connection string is present and server is reachable.
    /// Returns null if MongoDB is not configured or not available.
    /// </summary>
    public static IMongoDatabase? Initialize(IConfiguration configuration, ILogger? logger = null)
    {
        var connectionString = configuration.GetConnectionString("MongoDB");
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        var databaseName = configuration["MongoDB:DatabaseName"] ?? "MedicalSystem";

        try
        {
            var client = new MongoClient(connectionString);
            var database = client.GetDatabase(databaseName);

            // Test connection with ping
            var pingResult = database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));
            return pingResult["ok"].AsDouble == 1.0 ? database : null;
        }
        catch (Exception ex) when (
            ex is MongoConnectionException ||
            ex is MongoAuthenticationException ||
            ex is TimeoutException)
        {
            logger?.LogWarning(ex, "MongoDB connection failed. Running without MongoDB.");
            //logger?.LogWarning(ex, "MongoDB unavailable. Connection string: {ConnectionString}", MaskConnectionString(connectionString));
            return null;
        }
    }

    //public static IMongoDatabase? Initialize()
    //{
    //    //mongdb is optional
    //    try
    //    {
    //        var client = new MongoClient("mongodb://localhost:27017");
    //        var database = client.GetDatabase("MedicalSystem");
    //        var pingResult = database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));
    //        return pingResult["ok"].AsDouble == 1 ? database : null;
    //    }
    //    catch (Exception)
    //    {
    //        return null;
    //    }
    //}
}
