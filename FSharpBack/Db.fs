﻿namespace RedDuck.Candleswap.Candles

open System
open System.Collections.Generic
open Dapper
open RedDuck.Candleswap.Candles.Types
open System.Data.SqlClient

module Db = 
    let private fetchCandlesSql = @"select datetime, resolutionSeconds, pairId, [open] as _open, high, low, [close], volume from candles
        where pairId = @pairId and resolutionSeconds = @resolutionSeconds"

    let private insertCandleSql =
        "insert into candles(datetime, resolutionSeconds, pairId, [open], high, low, [close], volume) "
        + "values (@datetime, @resolutionSeconds, @pairId, @_open, @high, @low, @close, @volume)"

    let private updateCandleSql =
        "update candles set [open] = @_open, high = @high, low = @low, [close] = @close, volume = @volume "
        + "where pairId = @pairId and resolutionSeconds = @resolutionSeconds and datetime = @datetime"

    let private getCandleByDatetimeSql =
        "select datetime, resolutionSeconds, token0Id, token1Id, [open], high, low, [close], volume"
        + "from candles"
        + "where datetime = @datetime"

    let private fetchPairsSql = "select * from pairs"

    let private insertPairSql = "insert into pairs(token0Id, token1Id) values(@token0Id, @token1Id)"

    let inline (=>) k v = k, box v

    let private dbQuery<'T> (connection: SqlConnection) (sql: string) (parameters: IDictionary<string, obj> option) =
        match parameters with
        | Some (p) -> connection.QueryAsync<'T>(sql, p)
        | None -> connection.QueryAsync<'T>(sql)

    let private dbExecute (connection: SqlConnection) (sql: string) (data: _) = connection.ExecuteAsync(sql, data)

    let fetchCandles connection pairId (resolutionSeconds: int) =
        async {
            let! candles =
                Async.AwaitTask
                <| dbQuery<DbCandle>
                    connection
                    fetchCandlesSql
                    (Some(
                        dict [ "pairId" => pairId
                               "resolutionSeconds" => resolutionSeconds ]
                    ))

            return candles
        }

    let fetchCandlesTask connection pairId (resolutionSeconds: int) =
        fetchCandles connection pairId resolutionSeconds
        |> Async.StartAsTask

    let addCandle connection candle =
        async {
            let! rowsChanged =
                Async.AwaitTask
                <| dbExecute connection insertCandleSql candle

            printfn "records added: %i" rowsChanged
        }

    let updateCandle connection candle =
        async {
            let! rowsChanged =
                Async.AwaitTask
                <| dbExecute connection updateCandleSql candle

            printfn "records added: %i" rowsChanged
        }

    let getCandleByDatetime connection datetime =
        async {
            let queryParams = Some <| dict [ "datetime" => datetime ]

            let! candle =
                Async.AwaitTask
                <| dbQuery connection getCandleByDatetimeSql queryParams

            return candle
        }

    let fetchPairsAsync connection = 
        async{
            return! dbQuery<Pair> connection fetchPairsSql None
                    |> Async.AwaitTask
        }

    let addPairAsync connection pair = 
        async{
            let! rowsChanged = dbExecute connection insertPairSql pair
                               |> Async.AwaitTask
            printfn "records added: %i" rowsChanged
        }

    let fetchPairAsync connection token0Id token1Id = 
        async{
            let! pairs = fetchPairsAsync connection
            return pairs |> Seq.tryFind (fun pair -> pair.token0Id = token0Id && pair.token1Id = token1Id)
        }

    let fetchPairOrCreateNewIfNotExists connection token0Id token1Id = 
        async{
            let! pair = fetchPairAsync connection token0Id token1Id
            match pair with
            | Some pair -> return pair
            | None -> let pair = {id = 0L; token0Id = token0Id; token1Id = token1Id}
                      do! addPairAsync connection pair
                      return pair
        }

    let AddPairsIfNotExistAsync connection pairs = 
        async{
            let! pairsFromDb = fetchPairsAsync connection
            for pair in pairs do
                let pairFromDb = 
                    pairsFromDb
                    |> Seq.tryFind(fun p -> p.token0Id = pair.token0Id && p.token1Id = pair.token1Id)
                if pairFromDb.IsNone
                then do! addPairAsync connection pair
        }
