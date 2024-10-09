VERSION=0.16.0

docker run --rm -v "${PWD}:/local" --network host -u $(id -u ${USER}):$(id -g ${USER})  openapitools/openapi-generator-cli generate \
-i http://localhost:5020/swagger/v1/swagger.json \
-g csharp \
-o /local/out --additional-properties=packageName=Coflnet.Payments.Client,packageVersion=$VERSION,licenseId=MIT,targetFramework=net6.0

cd out
path=src/Coflnet.Payments.Client/Coflnet.Payments.Client.csproj
sed -i 's/GIT_USER_ID/Coflnet/g' $path
sed -i 's/GIT_REPO_ID/Payments/g' $path
sed -i 's/>OpenAPI/>Coflnet/g' $path
sed -i 's@annotations</Nullable>@annotations</Nullable>\n    <PackageReadmeFile>README.md</PackageReadmeFile>@g' $path
sed -i '34i    <None Include="../../../../README.md" Pack="true" PackagePath="\"/>' $path


function replace_flags() {
    local path=$1

    sed -i 's/= 1/= 0/g' $path
    sed -i 's/= 2/= 1/g' $path
    sed -i 's/= 3/= 2/g' $path
    sed -i 's/= 4/= 4/g' $path
    sed -i 's/= 5/= 8/g' $path
    sed -i 's/= 6/= 16/g' $path
    sed -i 's/= 7/= 32/g' $path

    sed -i 's/JsonConverter(typeof(StringEnumConverter))/JsonConverter(typeof(StringEnumConverter)), Flags/' $path
}

FlagFile="src/Coflnet.Payments.Client/Model/RuleFlags.cs"
replace_flags $FlagFile

TypeFile="src/Coflnet.Payments.Client/Model/ProductType.cs"
replace_flags $TypeFile


dotnet pack
cp src/Coflnet.Payments.Client/bin/Release/Coflnet.Payments.Client.*.nupkg ..
