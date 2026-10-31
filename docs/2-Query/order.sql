SET NOCOUNT ON;
SET XACT_ABORT ON; -- บังคับ Rollback อัตโนมัติทันทีหากเกิด Run-time error ร้ายแรง

-- 1. กำหนดตัวแปร Parameter
DECLARE @CustomerId INT = 101;
DECLARE @NewOrderId BIGINT;            -- Orders.OrderId เป็น BIGINT
DECLARE @Now DATETIME2 = SYSUTCDATETIME();

-- 2. สร้าง Table Variable จำลองรายการสินค้าที่สั่งซื้อเข้ามา
DECLARE @RequestedItems TABLE (
    ProductId INT PRIMARY KEY,
    Quantity INT NOT NULL CHECK (Quantity > 0)
);

INSERT INTO @RequestedItems (ProductId, Quantity)
VALUES
    (1001, 2),
    (1002, 1);

-- 3. เริ่มต้น Transaction
BEGIN TRY
    BEGIN TRANSACTION;

    -- ตรวจสอบว่าลูกค้ามีอยู่จริง
    IF NOT EXISTS (SELECT 1 FROM Customers WHERE CustomerId = @CustomerId)
    BEGIN
        THROW 50001, 'Customer does not exist.', 1;
    END

    -- ตรวจสอบว่าสินค้าทุกชิ้นมีอยู่จริงในตาราง Products หรือไม่
    IF EXISTS (
        SELECT 1
        FROM @RequestedItems r
        LEFT JOIN Products p ON r.ProductId = p.ProductId
        WHERE p.ProductId IS NULL
    )
    BEGIN
        THROW 50002, 'Some requested products do not exist in the catalog.', 1;
    END

    -- ตรวจสอบว่าสินค้าทุกชิ้นเปิดขายอยู่ (IsActive = 1)
    IF EXISTS (
        SELECT 1
        FROM @RequestedItems r
        INNER JOIN Products p ON r.ProductId = p.ProductId
        WHERE p.IsActive = 0
    )
    BEGIN
        THROW 50003, 'Some requested products are inactive.', 1;
    END

    -- ตัดสต็อกแบบ Atomic: เงื่อนไข StockQuantity >= Quantity อยู่ใน UPDATE เดียวกัน
    -- ทำให้ 2 Transaction ที่ซื้อสินค้าชิ้นสุดท้ายพร้อมกันไม่สามารถตัดสต็อกซ้ำได้ (กัน Overselling)
    -- UPDATE ถือ Exclusive Lock ของแถวสินค้าไว้จนจบ Transaction ราคาที่อ่านด้านล่างจึงไม่เปลี่ยนระหว่างทาง
    UPDATE p
    SET p.StockQuantity = p.StockQuantity - r.Quantity
    FROM Products p
    INNER JOIN @RequestedItems r ON r.ProductId = p.ProductId
    WHERE p.IsActive = 1
      AND p.StockQuantity >= r.Quantity;

    -- ถ้าจำนวนแถวที่ถูกตัดสต็อกไม่เท่ากับจำนวนสินค้าที่สั่ง แปลว่ามีสินค้าบางรายการสต็อกไม่พอ
    IF @@ROWCOUNT <> (SELECT COUNT(*) FROM @RequestedItems)
    BEGIN
        THROW 50004, 'Insufficient stock for one or more requested products.', 1;
    END

    -- คำนวณยอดรวม (TotalAmount) จากราคาปัจจุบันในตาราง Products (ไม่ Hard-code ราคา/ยอดรวม)
    DECLARE @CalculatedTotal DECIMAL(18, 2);

    SELECT @CalculatedTotal = SUM(r.Quantity * p.Price)
    FROM @RequestedItems r
    INNER JOIN Products p ON r.ProductId = p.ProductId;

    -- 4. บันทึกข้อมูลลงตาราง Orders โดยสถานะเริ่มต้นเป็น Pending
    INSERT INTO Orders (CustomerId, OrderStatus, TotalAmount, CreatedAt, UpdatedAt)
    VALUES (@CustomerId, 'Pending', @CalculatedTotal, @Now, @Now);

    -- ดึง OrderId ที่เพิ่งถูก Generate ขึ้นมา (SCOPE_IDENTITY คืนค่าเฉพาะของ Scope นี้ ไม่ปนกับ Session อื่น)
    SET @NewOrderId = CAST(SCOPE_IDENTITY() AS BIGINT);

    -- 5. บันทึกข้อมูลลงตาราง OrderItems โดยเก็บ Snapshot ราคา ณ ตอนที่ซื้อ (UnitPrice)
    INSERT INTO OrderItems (OrderId, ProductId, Quantity, UnitPrice)
    SELECT
        @NewOrderId,
        r.ProductId,
        r.Quantity,
        p.Price
    FROM @RequestedItems r
    INNER JOIN Products p ON r.ProductId = p.ProductId;

    -- หากทุกอย่างทำงานสำเร็จ ให้ Commit การทำงาน
    COMMIT TRANSACTION;

    -- 6. ส่งคืน (Return) ค่า OrderId ให้กับ Client / Application Caller
    SELECT @NewOrderId AS CreatedOrderId;

END TRY
BEGIN CATCH
    -- 7. จัดการกรณีเกิดข้อผิดพลาด: ตรวจสอบและ Rollback ทันที
    IF @@TRANCOUNT > 0
    BEGIN
        ROLLBACK TRANSACTION;
    END;

    -- ส่งต่อ Error message เพื่อแจ้งเตือน Application Layer
    THROW;
END CATCH;
