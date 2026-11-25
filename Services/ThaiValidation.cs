using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

namespace GoodHotel.Services;

/// <summary>
/// ข้อความตรวจฟอร์มภาษาไทยเป็นค่าเริ่มต้นทั้งระบบ (เดิมฟอร์มที่ไม่ได้ใส่ ErrorMessage ขึ้นภาษาอังกฤษ)
/// ฟอร์มที่ใส่ ErrorMessage เองใช้ข้อความนั้นเหมือนเดิม
/// </summary>
public sealed class ThaiValidation : IValidationMetadataProvider
{
    // แปลข้อความของทุกช่องในฟอร์ม
    public void CreateValidationMetadata(ValidationMetadataProviderContext context)
    {
        var validators = context.ValidationMetadata.ValidatorMetadata;

        // ช่องที่เป็น bool/ตัวเลข/วันที่แบบห้ามว่าง MVC จะใส่ Required ภาษาอังกฤษให้ฝั่งหน้าเว็บเอง จึงใส่ของเราแทน
        var type = context.Key.ModelType;
        if (context.Key.MetadataKind == ModelMetadataKind.Property
            && type.IsValueType && Nullable.GetUnderlyingType(type) == null
            && !validators.OfType<RequiredAttribute>().Any())
            validators.Add(new RequiredAttribute());

        foreach (var attribute in validators.OfType<ValidationAttribute>())
            Translate(attribute);
    }

    // แปลข้อความของกติกาหนึ่งข้อ
    public static void Translate(ValidationAttribute attribute)
    {
        if (attribute.ErrorMessage != null || attribute.ErrorMessageResourceName != null) return;
        attribute.ErrorMessage = attribute switch
        {
            RequiredAttribute => "กรุณากรอกข้อมูลช่องนี้",
            StringLengthAttribute { MinimumLength: > 0 } => "ต้องยาว {2}-{1} ตัวอักษร",
            StringLengthAttribute => "ต้องยาวไม่เกิน {1} ตัวอักษร",
            RangeAttribute { Maximum: int.MaxValue } => "ต้องไม่น้อยกว่า {1}",
            RangeAttribute => "ต้องอยู่ระหว่าง {1} ถึง {2}",
            EmailAddressAttribute => "รูปแบบอีเมลไม่ถูกต้อง",
            CompareAttribute => "ข้อมูลทั้งสองช่องไม่ตรงกัน",
            RegularExpressionAttribute => "รูปแบบข้อมูลไม่ถูกต้อง",
            _ => null
        };
    }

    // ติดตั้งข้อความไทยให้ MVC (เรียกจาก Program.cs)
    public static void Configure(MvcOptions options)
    {
        options.ModelMetadataDetailsProviders.Add(new ThaiValidation());
        var messages = options.ModelBindingMessageProvider;
        messages.SetValueMustNotBeNullAccessor(_ => "กรุณากรอกข้อมูลช่องนี้");
        messages.SetMissingBindRequiredValueAccessor(_ => "กรุณากรอกข้อมูลช่องนี้");
        messages.SetMissingKeyOrValueAccessor(() => "กรุณากรอกข้อมูลช่องนี้");
        messages.SetMissingRequestBodyRequiredValueAccessor(() => "ไม่พบข้อมูลที่ส่งมา");
        messages.SetAttemptedValueIsInvalidAccessor((_, _) => "ข้อมูลไม่ถูกต้อง");
        messages.SetNonPropertyAttemptedValueIsInvalidAccessor(_ => "ข้อมูลไม่ถูกต้อง");
        messages.SetUnknownValueIsInvalidAccessor(_ => "ข้อมูลไม่ถูกต้อง");
        messages.SetNonPropertyUnknownValueIsInvalidAccessor(() => "ข้อมูลไม่ถูกต้อง");
        messages.SetValueIsInvalidAccessor(_ => "ข้อมูลไม่ถูกต้อง");
        messages.SetValueMustBeANumberAccessor(_ => "กรุณากรอกเป็นตัวเลข");
        messages.SetNonPropertyValueMustBeANumberAccessor(() => "กรุณากรอกเป็นตัวเลข");
    }
}
