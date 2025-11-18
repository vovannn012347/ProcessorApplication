using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;

using Common.Models.NodeContext;

namespace ProcessorApplication.Services.p2pNode;

public static class ActionExtensionMethods
{
    public static DateTime GetLastSomething(this DateTimeStore dateTimeStore, string what)
    {
        return dateTimeStore.TryGetValue(what, out var dt) ? dt : dateTimeStore[what] = DateTime.MinValue;
    }
    public static DateTimeStore SetLastSomething(this DateTimeStore dateTimeStore, string what, DateTime value)
    {
        dateTimeStore[what] = value;
        return dateTimeStore;
    }

    public const string LastCleanup = "LastCleanup";
    public static DateTime GetLastCleanup(this DateTimeStore dateTimeStore)
    {
        return dateTimeStore.TryGetValue(LastCleanup, out var dt) ? dt : dateTimeStore[LastCleanup] = DateTime.MinValue;
    }
    public static DateTimeStore SetLastCleanup(this DateTimeStore dateTimeStore, DateTime value)
    {
        dateTimeStore[LastCleanup] = value;
        return dateTimeStore;
    }



    public const string LastNodeOrdering = "LastNodeOrdering";

    public static DateTime GetLastNodeOrdering(this DateTimeStore dateTimeStore)
    {
        return dateTimeStore.TryGetValue(LastNodeOrdering, out var dt) ? dt : dateTimeStore[LastNodeOrdering] = DateTime.MinValue;
    }
    public static DateTimeStore SetLastNodeOrdering(this DateTimeStore dateTimeStore, DateTime value)
    {
        dateTimeStore[LastNodeOrdering] = value;
        return dateTimeStore;
    }


    public const string LastPersist = "LastPersist";

    public static DateTime GetLastPersist(this DateTimeStore dateTimeStore)
    {
        return dateTimeStore.TryGetValue(LastPersist, out var dt) ? dt : dateTimeStore[LastPersist] = DateTime.MinValue;
    }

    public static DateTimeStore SetLastPersist(this DateTimeStore dateTimeStore, DateTime value)
    {
        dateTimeStore[LastPersist] = value;
        return dateTimeStore;
    }

    public const string LastAdversise = "LastAdversise";

    public static DateTime GetLastAdvertise(this DateTimeStore dateTimeStore)
    {
        return dateTimeStore.TryGetValue(LastAdversise, out var dt) ? dt : dateTimeStore[LastAdversise] = DateTime.MinValue;
    }

    public static DateTimeStore SetLastAdvertise(this DateTimeStore dateTimeStore, DateTime value)
    {
        dateTimeStore[LastAdversise] = value;
        return dateTimeStore;
    }


    public const string LastGossip = "LastGossip";

    public static DateTime GetLastGossip(this DateTimeStore dateTimeStore)
    {
        return dateTimeStore.TryGetValue(LastGossip, out var dt) ? dt : dateTimeStore[LastGossip] = DateTime.MinValue;
    }

    public static DateTimeStore SetLastGossip(this DateTimeStore dateTimeStore, DateTime value)
    {
        dateTimeStore[LastGossip] = value;
        return dateTimeStore;
    }


    public const string LastTrackerGossip = "LastTrackerGossip";

    public static DateTime GetLastTrackerGossip(this DateTimeStore dateTimeStore)
    {
        return dateTimeStore.TryGetValue(LastTrackerGossip, out var dt) ? dt : dateTimeStore[LastTrackerGossip] = DateTime.MinValue;
    }

    public static DateTimeStore SetLastTrackerGossip(this DateTimeStore dateTimeStore, DateTime value)
    {
        dateTimeStore[LastTrackerGossip] = value;
        return dateTimeStore;
    }


    public const string LastPing = "LastPing";

    public static DateTime GetLastPing(this DateTimeStore dateTimeStore)
    {
        return dateTimeStore.TryGetValue(LastPing, out var dt) ? dt : dateTimeStore[LastPing] = DateTime.MinValue;
    }

    public static DateTimeStore SetLastPing(this DateTimeStore dateTimeStore, DateTime value)
    {
        dateTimeStore[LastPing] = value;
        return dateTimeStore;
    }


    public const string LastHello = "LastHello";

    public static DateTime GetLastHello(this DateTimeStore dateTimeStore)
    {
        return dateTimeStore.TryGetValue(LastHello, out var dt) ? dt : dateTimeStore[LastHello] = DateTime.MinValue;
    }

    public static DateTimeStore SetLastHello(this DateTimeStore dateTimeStore, DateTime value)
    {
        dateTimeStore[LastHello] = value;
        return dateTimeStore;
    }
}
