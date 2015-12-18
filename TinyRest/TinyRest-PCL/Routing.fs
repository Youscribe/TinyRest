﻿module public Routing

open System
open System.Text
open System.Text.RegularExpressions
open Microsoft.FSharp.Reflection

type FormatParsed =
    | StringPart
    | CharPart
    | BoolPart
    | IntPart
    | DecimalPart
    | HexaPart
    member x.Parse (s:string) : obj =
        match x with
        | StringPart    -> s |> box
        | CharPart      -> s.Chars 0 |> box
        | BoolPart      -> s |> Boolean.Parse |> box
        | IntPart       -> s |> int64 |> box
        | DecimalPart   -> s |> float |> box
        | HexaPart      -> 
            let str = s.ToLower() 
            if str.StartsWith "0x"
            then s |> int64 |> box
            else ("0x" + s) |> int64 |> box

type FormatPart =
    | Constant  of string
    | Parsed    of FormatParsed
    member x.Match text : (obj*Type) option=
        let parseInt s = 
            let r:int64 ref = ref 0L
            if Int64.TryParse(s, r) then Some (box !r, typeof<Int64>) else None
        match x with
        | Constant s -> if s = text then Some (box s, typeof<string>) else None
        | Parsed p ->
            match p with
            | StringPart    -> Some (box text, typeof<string>)
            | CharPart      -> 
                if text = null || text.Length > 1 
                then None
                else Some (text.Chars 0 |> box, typeof<char>)
            | BoolPart      -> 
                let r:bool ref = ref false
                if bool.TryParse(text, r)
                then Some (box !r, typeof<bool>)
                else None
            | IntPart       -> parseInt text
            | DecimalPart   -> 
                let r:decimal ref = ref 0m
                if Decimal.TryParse(text, r)
                then Some (box !r, typeof<Decimal>)
                else None
            | HexaPart      -> 
                match text.ToLower() with
                | str when str.StartsWith "0x" |> not -> "0x" + str
                | str -> str
                |> parseInt

type RouteFormat = 
    { Parts:FormatPart list 
      Type:Type }

type FormatParser = 
    { Parts:FormatPart list ref
      Buffer:char list ref
      Format:string
      Position:int ref }
    static member Create f =
        { Parts = ref List.empty
          Buffer = ref List.empty
          Format = f
          Position = ref 0 }
    member x.Acc (s:string) =
        x.Buffer := !x.Buffer @ (s.ToCharArray() |> Seq.toList)
    member x.Acc (c:char) =
        x.Buffer := !x.Buffer @ [c]
    member private x.Finished () =
        !x.Position >= x.Format.Length
    member x.Next() =
        if x.Finished() |> not then
            x.Format.Chars !x.Position |> x.Acc
            x.Position := !x.Position + 1
    member x.PreviewNext() =
        if !x.Position >= x.Format.Length - 1
        then None
        else Some (x.Format.Chars (!x.Position))
    member x.Push t =
        x.Parts := !x.Parts @ t
        x.Buffer := List.empty
    member x.StringBuffer skip =
        !x.Buffer |> Seq.skip skip |> Seq.toArray |> String
    member x.Parse (ty:Type) =
        while x.Finished() |> not do
            x.Next()
            match !x.Buffer with
            | '%' :: '%' :: _ -> x.Push [Constant (x.StringBuffer 1)]
            | '%' :: 'b' :: _ -> x.Push [Parsed BoolPart]
            | '%' :: 'i' :: _
            | '%' :: 'u' :: _
            | '%' :: 'd' :: _ -> x.Push [Parsed IntPart]
            | '%' :: 'c' :: _ -> x.Push [Parsed StringPart]
            | '%' :: 's' :: _ -> x.Push [Parsed StringPart]
            | '%' :: 'e' :: _
            | '%' :: 'E' :: _
            | '%' :: 'f' :: _
            | '%' :: 'F' :: _
            | '%' :: 'g' :: _
            | '%' :: 'G' :: _ -> x.Push [Parsed DecimalPart]
            | '%' :: 'x' :: _
            | '%' :: 'X' :: _ -> x.Push [Parsed HexaPart]
            | c :: _ -> 
                let n = x.PreviewNext()
                match n with
                | Some '%' -> x.Push [Constant (x.StringBuffer 0)]
                | _ -> ()
            | _ -> ()
        if !x.Buffer |> Seq.isEmpty |> not then x.Push [Constant (x.StringBuffer 0)]
        { Parts = !x.Parts; Type = ty }

type IRouteHandler<'t> =
    abstract member TryHandle : string -> unit

type RouteFormatHandler<'t> = 
    { Format : RouteFormat
      Fun : 't -> unit }
    static member New f h =
        { Format = f
          Fun = h }
    member x.Match url =
        let rec skipNextConstant (text:string) (parts:FormatPart list) (acc:string list) =
            match parts with
            | Constant s :: t ->
                let i = text.ToLowerInvariant().IndexOf(s.ToLowerInvariant())
                if i >= 0 then
                    let start = text.Substring (0, i)
                    let j = i + s.Length
                    let rest = text.Substring (j, text.Length - j)
                    skipNextConstant rest t (if i > 0 then acc @ [start] else acc)
                else None
            | Parsed _ :: [] -> Some (acc @ [text])
            | _ :: t -> skipNextConstant text t acc
            | [] -> if text.Length = 0 then Some acc else None
        let parsed = x.Format.Parts
                        |> List.filter (fun p -> match p with | Parsed _ -> true | _ -> false)
        match skipNextConstant url x.Format.Parts [] with
        | None -> None
        | Some values when values.Length <> parsed.Length -> None
        | Some values ->
            let types = FSharpType.GetTupleElements(x.Format.Type)
            let results = parsed
                            |> List.zip values
                            |> List.choose (fun (v,p) -> p.Match v)
                            |> Seq.zip types
                            |> Seq.map (
                            fun (tupleType,(o, t)) -> 
                                match tupleType with
                                | v when v = typeof<int32> ->
                                    int32(unbox<int64> o) |> box
                                | v when v = typeof<uint32> ->
                                    uint32(unbox<int64> o) |> box
                                | _ -> o
                            )
                            |> Seq.toArray
            Some (FSharpValue.MakeTuple(results, x.Format.Type))
    member x.Invoke a = x.Fun a
    interface IRouteHandler<'t> with
        member x.TryHandle url = 
            let m = x.Match url
            match m with
            | Some tuple -> x.Invoke (tuple :?> 't)
            | None -> ()

let urlFormat (pf : PrintfFormat<_,_,_,_,'t>) (h : 't -> unit) =
    let f = pf.Value |> FormatParser.Create |> fun p -> p.Parse(typeof<'t>)
    RouteFormatHandler<'t>.New f h

