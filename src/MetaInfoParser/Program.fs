﻿open System

module Models = 
    type Field = {
        Name: string
        Type: string
        IsCollection : bool
    }

    type Entity = {
        Name: string
        Fields: Field list
    }

module Async = 
    let map projection computation = async {
        let! x = computation
        return projection x
    }
    let bind binder computation = async.Bind (computation, binder)

module Network = 
    open System.Net
    
    let download url =
        let client = new WebClient(UseDefaultCredentials = true, Credentials = new NetworkCredential("admin", "admin"))
        client.AsyncDownloadString <| Uri url

module DataSource = 
    open FSharp.Data
    open Models
    open Network

    type MetaInfoIndex = XmlProvider<"meta-index.xml", InferTypesFromValues = true>
    type EntityMetaInfo = XmlProvider<"meta-general.xml", InferTypesFromValues = true, SampleIsList = true>

    let META_API_ENDPOINT = "http://localhost/targetprocess/api/v1/Index/meta?format=xml"

    let getEntities () = 
        let inline buildField isCollection (propertyInfo : ^a) = 
            if (^a : (member IsDeprecated : bool) (propertyInfo))
                then None
                else Some {
                    Name = (^a : (member Name : string) (propertyInfo))
                    Type = (^a : (member Type : string) (propertyInfo))
                    IsCollection = isCollection
                }

        let buildFields entityMetaText = 
            let entityMeta = EntityMetaInfo.Parse entityMetaText
            let props = entityMeta.ResourceMetadataPropertiesDescription
            
            let inline choose getFields isCollection = 
                Option.toList
                >> Seq.collect getFields
                >> Seq.choose (buildField isCollection)

            seq {
                yield! 
                    props.ResourceMetadataPropertiesResourceValuesDescription |> Some
                    |> choose (fun x -> x.ResourceFieldMetadataDescriptions) false

                yield! 
                    props.ResourceMetadataPropertiesResourceReferencesDescription
                    |> choose (fun x -> x.ResourceFieldMetadataDescriptions) false
                yield!
                    props.ResourceMetadataPropertiesResourceCollectionsDescription
                    |> choose (fun x -> x.ResourceCollecitonFieldMetadataDescriptions) true
            } 
            |> Seq.sortBy (fun x -> x.Name)
            |> List.ofSeq

        let downloadFields uri = 
            uri + "?format=xml"
            |> download
            |> Async.map buildFields

        download META_API_ENDPOINT
        |> Async.bind (
            MetaInfoIndex.Parse
            >> fun x -> x.ResourceMetadataDescriptions
            >> Seq.map (fun entityMeta ->
                entityMeta.Uri
                |> downloadFields
                |> Async.map (fun fs -> {Name = entityMeta.Name; Fields = fs}))
            >> Async.Parallel)
        |> Async.RunSynchronously

module Converter = 
    open Models

    let join separator (xs : string seq) = String.Join (separator, xs)
    let toLower (s : string) = s.ToLowerInvariant()

    let convert  = 
        let convertField (field : Field) = 
            sprintf "{ \"name\": \"%s\", \"type\": \"%s\", \"isCollection\": %b }" field.Name field.Type field.IsCollection

        let convertSingle entity = 
            entity.Fields 
            |> Seq.map convertField
            |> Seq.map (fun s -> "    " + s)
            |> join ",\r\n"
            |> sprintf "\"%s\": [\r\n%s\r\n]" (toLower entity.Name)
        
        Seq.map convertSingle
        >> join ", \r\n"
        >> sprintf "{\r\n%s\r\n}"

module Output = 
    let wrapRequire = sprintf "define(function(require) {\r\n    return %s;\r\n});"
    let addESLintFlags = sprintf "/*eslint quotes:0*/\r\n%s"
    let addJSHintFlags = sprintf "/*jshint quotmark:false*/\r\n%s"
    let addHeader = sprintf "// AUTO-GENERATED with https://github.com/TargetProcess/EntityFilterMetaInfoParser\r\n%s"

DataSource.getEntities() 
|> Converter.convert
|> Output.wrapRequire
|> Output.addESLintFlags
|> Output.addJSHintFlags
|> Output.addHeader
|> printfn "%s"
