﻿/// The MIT License (MIT)
/// Copyright (c) 2016 Bazinga Technologies Inc
module FSharp.Data.GraphQL.Linq

open System
open System.Collections
open System.Collections.Generic
open System.Linq
open System.Linq.Expressions
open FSharp.Reflection
open FSharp.Quotations.Evaluator
open FSharp.Data.GraphQL.Types
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.Patterns

let private unwrap (resolve: Resolve) inParam: Expression =
    let (Lambda(_, inner)) = resolve.Expr
    match inner with
    | Lambda(var1, PropertyGet(Some(var2), propInfo, args)) ->
        upcast Expression.Property(inParam, propInfo)
    | other -> QuotationEvaluator.ToLinqExpression other

let private (|Object|Record|NotSupported|) (t: Type) =
    if FSharpType.IsRecord t then Record
    elif FSharpType.IsTuple t then NotSupported
    elif FSharpType.IsUnion t then NotSupported
    else Object
    
let private unwrapType = function
    | List inner -> inner.Type
    | Nullable inner -> inner.Type
    | tdef -> tdef.Type

let castTo tCollection callExpr : Expression = 
    match tCollection with
    | Gen.List tRes ->
        let cast = Gen.listOfSeq.MakeGenericMethod [| tRes |]
        upcast Expression.Call(null, cast, [ callExpr ])
    | Gen.Array tRes ->
        let cast = Gen.arrayOfSeq.MakeGenericMethod [| tRes |]
        upcast Expression.Call(null, cast, [ callExpr ])
    | Gen.Set tRes ->
        let cast = Gen.setOfSeq.MakeGenericMethod [| tRes |]
        upcast Expression.Call(null, cast, [ callExpr ])
    | _ -> callExpr
        
let rec private eval tIn info (inputExpr: Expression) : Expression =
    match info.Kind with
    | ResolveValue ->  inputExpr
    | SelectFields fields ->
        // construct new object initializer with bindings as list of assignments for each field
        let returnedType = unwrapType info.Definition.TypeDef
        constructObject returnedType fields inputExpr
    | ResolveCollection inner ->
        // apply Select on the Expr target
        let tResult = inner.ReturnDef.Type
        // create a call, that will return either IEnumerable`1 or IQueryable`1
        let call =
            match tIn with
            | Gen.Queryable tSource ->
                let p0 = Expression.Parameter(tSource)
                let body = eval inner.ParentDef.Type inner p0
                // call method - ((IQueryable<tSource>)inputExpr).Select(p0 => body)
                Expression.Call(
                    // Select<tSource, tResult> - method to invoke
                    QueryableMethods.Select.MakeGenericMethod [| tSource; tResult |], 
                    // `this` param - Convert(inputValue, IQueryable<tSource>)
                    Expression.Convert(inputExpr, QueryableMethods.Type.MakeGenericType [| tSource |]), 
                    // `mapFunc` param - (p0 => body )
                    Expression.Lambda(body, p0))

            | Gen.Enumerable tSource ->
                let p0 = Expression.Parameter(tSource)
                let body = eval inner.ParentDef.Type inner p0
                // call method - ((IEnuerable<tSource>)inputExpr).Select(p0 => body)
                Expression.Call(
                    // Select<tSource, tResult> - method to invoke
                    EnumerableMethods.Select.MakeGenericMethod [| tSource; tResult |], 
                    // `this` param - Convert(inputValue, IEnumerable<tSource>)
                    Expression.Convert(inputExpr, EnumerableMethods.Type.MakeGenericType [| tSource |]), 
                    // `mapFunc` param - (p0 => ... )
                    Expression.Lambda(body, p0))

            | _ -> raise (InvalidOperationException <| sprintf "Type %O is not enumerable" tIn)
        // enhance call with cast to result type
        castTo tIn call

and private constructObject (t: Type) (infos: ExecutionInfo list) inputExpr : Expression =
    let fieldMap = Dictionary()
    infos |> List.iter (fun f -> fieldMap.Add(f.Definition.Name.ToLower(), f)) 
    let ctor =
        match t with
        | Record -> FSharpValue.PreComputeRecordConstructorInfo t
        | Object -> 
            let fields = infos |> List.toArray |> Array.map (fun info -> info.Definition.Name)
            ReflectionHelper.matchConstructor t fields
        | NotSupported ->
            raise <| NotSupportedException (sprintf "LINQ conversion for type %O is not supported. Only POCOs and records are allowed." t)
    // try to match constructor arguments AND remove them from fieldMap
    let ctorArgs =
        ctor.GetParameters()
        |> Array.map (fun parameter -> 
            let paramName = parameter.Name.ToLower ()
            match fieldMap.TryGetValue paramName with
            | true, info -> 
                fieldMap.Remove paramName |> ignore
                let expr = unwrap info.Definition.Resolve inputExpr
                eval info.ReturnDef.Type info expr
            | false, _ -> upcast Expression.Default parameter.ParameterType)
    // if all query fields matched into constructor, invoke it with new expr
    // otherwise make member init expr, and pass remaining fields as member bindings
    if fieldMap.Count = 0
    then upcast Expression.New(ctor, ctorArgs)
    else 
        let members = 
            t.GetMembers()
            |> Array.map (fun m -> (m.Name.ToLower(), m))
            |> Map.ofArray
        let memberBindings : MemberBinding seq = 
            fieldMap
            |> Seq.map (fun kv -> 
                let m = Map.find kv.Key members
                upcast Expression.Bind(m, eval kv.Value.ParentDef.Type kv.Value inputExpr))
        upcast Expression.MemberInit(Expression.New(ctor, ctorArgs), memberBindings)        
        
let rec private toLinq info (query: IQueryable<'Source>) : IQueryable<'Result> =
    let collectionType = query.GetType()
    let parameter = Expression.Parameter(collectionType)
    let expr = eval collectionType info parameter
    let compiled =
        match expr with
        | :? MethodCallExpression as call -> 
            let lambda = Expression.Lambda(call, [| parameter |])
            let compiled = lambda.Compile()
            compiled
        | selector -> 
            let tSource = typeof<'Source>
            let tResult = typeof<'Result>
            let mSelect = QueryableMethods.Select.MakeGenericMethod [| tSource; tResult |]
            let destinationType = QueryableMethods.Type.MakeGenericType [| typeof<'Result> |]
            let call = Expression.Call(mSelect, Expression.Convert(parameter, destinationType), selector)
            Expression.Lambda(call, [| parameter |]).Compile()
    downcast compiled.DynamicInvoke [| box query |]

type FSharp.Data.GraphQL.Types.ExecutionInfo with
    member this.ToLinq(source: IQueryable<'Source>) : IQueryable<'Result> = 
        toLinq this source
        